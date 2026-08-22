using System.Data.Common;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuditTrailGuaranteesTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_failure_on_the_audit_insert_rolls_back_the_whole_publication()
    {
        var author = fixture.CreateAuthorClient("author-at-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        // Same handler the endpoint uses, over a connection that fails exactly
        // when the audit event is inserted: if the publication survived, the
        // status flip and the audit row would not share a transaction.
        DbContextOptions<TemplateManagementDbContext> options =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(fixture.PostgresConnectionString)
                .AddInterceptors(new FailOnAuditInsertInterceptor())
                .Options;
        await using (var db = new TemplateManagementDbContext(options))
        {
            var handler = new PublishTemplateVersion.Handler(
                db,
                new TemplateVersionAnalyzer(new ScribanTemplateEngine(Options.Create(new TemplatingOptions()))),
                TimeProvider.System,
                NullLogger<PublishTemplateVersion.Handler>.Instance);

            DbUpdateException exception = await Should.ThrowAsync<DbUpdateException>(
                () => handler.HandleAsync(key, version, "publisher-at-1", CancellationToken.None));
            exception.InnerException.ShouldBeOfType<InvalidOperationException>();
        }

        HttpResponseMessage versionResponse = await author.GetAsync($"/v1/templates/{key}/versions/{version}");
        versionResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TemplateApi.ReadJsonAsync(versionResponse)).GetProperty("status").GetString().ShouldBe("draft");
        await fixture.ExecuteDbAsync(async db =>
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
    public async Task The_append_only_trigger_rejects_updates_on_audit_events()
    {
        var author = fixture.CreateAuthorClient("author-at-2");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteDbAsync(async db =>
        {
            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlAsync(
                    $"UPDATE templatemanagement.audit_event SET actor_id = 'tampered' WHERE entity_id = {key}"));
            exception.Message.ShouldContain("append-only");
        });
    }

    [RequiresDockerFact]
    public async Task The_append_only_trigger_rejects_deletes_on_audit_events()
    {
        var author = fixture.CreateAuthorClient("author-at-3");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteDbAsync(async db =>
        {
            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlAsync(
                    $"DELETE FROM templatemanagement.audit_event WHERE entity_id = {key}"));
            exception.Message.ShouldContain("append-only");
        });
    }

    /// <summary>Fails the command that inserts the audit event, after earlier statements were issued.</summary>
    private sealed class FailOnAuditInsertInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            ThrowOnAuditInsert(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnAuditInsert(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            ThrowOnAuditInsert(command);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private static void ThrowOnAuditInsert(DbCommand command)
        {
            if (command.CommandText.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains("audit_event", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Falha injetada antes do insert do evento de auditoria.");
            }
        }
    }
}
