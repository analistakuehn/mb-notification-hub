using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;
using NotificationHub.Api.Modules.TemplateManagement.Features.Templates;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuditTrailGuaranteesTests(TemplateManagementApiFixture fixture)
{
    // Far beyond the provisioned partition horizon, so the audit insert fails
    // in the database itself: the real failure mode a missing monthly
    // partition produces in production.
    private static readonly DateTimeOffset BeyondPartitionCoverage =
        new(2100, 1, 15, 12, 0, 0, TimeSpan.Zero);

    [RequiresDockerFact]
    public async Task A_failure_on_the_audit_insert_rolls_back_the_whole_publication()
    {
        HttpClient author = fixture.CreateAuthorClient("author-at-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        // Same handler the endpoint uses, with a clock pointing at a month
        // without a partition: if the publication survived the failed audit
        // insert, the status flip and the audit row would not share a
        // transaction.
        DbContextOptions<TemplateManagementDbContext> options =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(fixture.PostgresConnectionString)
                .Options;
        await using (var db = new TemplateManagementDbContext(options))
        {
            var handler = new PublishTemplateVersion.Handler(
                db,
                new TransactionalAuditTrail(),
                new TemplateVersionAnalyzer(new ScribanTemplateEngine(Options.Create(new TemplatingOptions()), new ScribanParseCache())),
                fixture.Services.GetRequiredService<PublishedReadCache>(),
                new FrozenClock(BeyondPartitionCoverage),
                NullLogger<PublishTemplateVersion.Handler>.Instance);

            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => handler.HandleAsync(key, version, "publisher-at-1", CancellationToken.None));
            exception.MessageText.ShouldContain("no partition");
        }

        HttpResponseMessage versionResponse = await author.GetAsync($"/v1/templates/{key}/versions/{version}");
        versionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TemplateApi.ReadJsonAsync(versionResponse)).GetProperty("status").GetString().ShouldBe("draft");
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            (await db.Approvals.AsNoTracking().AnyAsync(candidate => candidate.SubjectId == key))
                .ShouldBeFalse();
            (await db.AuditEvents.AsNoTracking().AnyAsync(candidate =>
                    candidate.Action == "template.version.published"
                    && candidate.EntityId == $"{key}:{version}"))
                .ShouldBeFalse();
        });
    }

    [RequiresDockerFact]
    public async Task A_failure_on_the_audit_insert_rolls_back_the_whole_policy_publication()
    {
        HttpClient author = fixture.CreateAuthorClient("author-at-4");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);

        // Same handler the endpoint uses, with a clock pointing at a month
        // without a partition: if the publication survived the failed audit
        // insert, the status flip, the approval and the audit row would not
        // share a transaction.
        DbContextOptions<TemplateManagementDbContext> options =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(fixture.PostgresConnectionString)
                .Options;
        await using (var db = new TemplateManagementDbContext(options))
        {
            var handler = new PublishClassPolicyVersion.Handler(
                db,
                new TransactionalAuditTrail(),
                fixture.Services.GetRequiredService<PublishedReadCache>(),
                new FrozenClock(BeyondPartitionCoverage),
                NullLogger<PublishClassPolicyVersion.Handler>.Instance);

            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => handler.HandleAsync(
                    application,
                    ClassPolicyApi.DefaultClass,
                    "publisher-at-4",
                    CancellationToken.None));
            exception.MessageText.ShouldContain("no partition");
        }

        HttpResponseMessage policy = await author.GetAsync(ClassPolicyApi.PolicyUrl(application));
        policy.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(policy);
        body.GetProperty("published").ValueKind.ShouldBe(JsonValueKind.Null);
        body.GetProperty("draft").GetProperty("status").GetString().ShouldBe("draft");
        await fixture.ExecuteAuditDbAsync(async db =>
            (await db.Approvals.AsNoTracking().AnyAsync(candidate =>
                candidate.SubjectId == $"{application}:{ClassPolicyApi.DefaultClass}"))
                .ShouldBeFalse());
    }

    [RequiresDockerFact]
    public async Task The_append_only_trigger_rejects_updates_on_audit_events()
    {
        HttpClient author = fixture.CreateAuthorClient("author-at-2");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlAsync(
                    $"UPDATE audit.audit_event SET actor_id = 'tampered' WHERE entity_id = {key}"));
            exception.Message.ShouldContain("append-only");
        });
    }

    [RequiresDockerFact]
    public async Task The_append_only_trigger_rejects_updates_on_the_chain_columns()
    {
        HttpClient author = fixture.CreateAuthorClient("author-at-5");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlAsync(
                    $"UPDATE audit.audit_event SET hash = NULL, prev_hash = NULL, canonical = NULL WHERE entity_id = {key}"));
            exception.Message.ShouldContain("append-only");
        });
    }

    [RequiresDockerFact]
    public async Task The_append_only_trigger_rejects_deletes_on_audit_events()
    {
        HttpClient author = fixture.CreateAuthorClient("author-at-3");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlAsync(
                    $"DELETE FROM audit.audit_event WHERE entity_id = {key}"));
            exception.Message.ShouldContain("append-only");
        });
    }

    private sealed class FrozenClock(DateTimeOffset instant) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => instant;
    }
}
