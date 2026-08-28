using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Features.Templates;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Governance operations racing each other. Interleaving is forced through a
/// command interceptor that commits the competing HTTP operation right before
/// the handler's own save executes, so the conflict window is deterministic
/// instead of depending on task scheduling.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class ConcurrentLifecycleConflictTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Two_concurrent_rollbacks_produce_one_publication_and_one_conflict()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cc-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-cc-1");
        (var key, var firstVersion) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, firstVersion);
        var secondVersion = await CreatePublishableDraftOnAsync(author, key);
        await TemplateApi.PublishAsync(publisher, key, secondVersion);

        HttpClient competingPublisher = fixture.CreatePublisherClient("publisher-cc-1c");
        var interceptor = new CompetingWriteInterceptor(
            verb: "INSERT",
            batchMarker: "template_version",
            competingAction: () => competingPublisher.PostAsJsonAsync(
                $"/v1/templates/{key}/rollback", new { toVersion = firstVersion }));

        Result<RollbackTemplate.Outcome> result;
        await using (TemplateManagementDbContext db = CreateDbContext(interceptor))
        {
            var handler = new RollbackTemplate.Handler(
                db,
                new TransactionalAuditTrail(),
                Analyzer(),
                fixture.Services.GetRequiredService<PublishedReadCache>(),
                TimeProvider.System,
                NullLogger<RollbackTemplate.Handler>.Instance);
            result = await handler.HandleAsync(
                new RollbackTemplate.Command(key, firstVersion, "publisher-cc-1b"),
                CancellationToken.None);
        }

        interceptor.CompetingResponse.ShouldNotBeNull().StatusCode.ShouldBe(HttpStatusCode.OK);
        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBeOneOf(
            ErrorCodes.PreconditionFailed,
            ErrorCodes.PublicationConflict);

        // Exactly one rollback landed: one new published clone of the first
        // version, and nothing from the losing side.
        await fixture.ExecuteDbAsync(async db =>
        {
            List<TemplateVersion> published = await db.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(TemplateKey.Trusted(key))
                .Where(candidate => candidate.Status == TemplateVersionStatus.Published)
                .ToListAsync();
            published.Count.ShouldBe(1);
            published[0].Version.ShouldBe(secondVersion + 1);
            published[0].RolledBackFrom.ShouldBe(firstVersion);
        });
    }

    [RequiresDockerFact]
    public async Task A_publication_racing_a_disable_has_exactly_one_winner()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cc-2");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpClient competingPublisher = fixture.CreatePublisherClient("publisher-cc-2c");
        // The publish save issues only updates now (the approval and the
        // audit event travel through the audit contract, outside this
        // context), so the deterministic window is the version update batch.
        var interceptor = new CompetingWriteInterceptor(
            verb: "UPDATE",
            batchMarker: "template_version",
            competingAction: () => competingPublisher.PostAsJsonAsync(
                $"/v1/templates/{key}/disable", new { reason = "retired", note = "desativação concorrente" }));

        Result<PublishTemplateVersion.Outcome> result;
        await using (TemplateManagementDbContext db = CreateDbContext(interceptor))
        {
            var handler = new PublishTemplateVersion.Handler(
                db,
                new TransactionalAuditTrail(),
                Analyzer(),
                fixture.Services.GetRequiredService<PublishedReadCache>(),
                TimeProvider.System,
                NullLogger<PublishTemplateVersion.Handler>.Instance);
            result = await handler.HandleAsync(key, version, "publisher-cc-2b", CancellationToken.None);
        }

        interceptor.CompetingResponse.ShouldNotBeNull().StatusCode.ShouldBe(HttpStatusCode.OK);
        result.IsFailure.ShouldBeTrue();
        DomainError.Describe(result.Error, result.ErrorKind).Code.ShouldBe(ErrorCodes.PreconditionFailed);

        // The disable won: the template is terminal and the draft never
        // published under a status that no longer accepts publications.
        await fixture.ExecuteDbAsync(async db =>
        {
            Template template = await db.Templates
                .AsNoTracking()
                .WhereKey(TemplateKey.Trusted(key))
                .SingleAsync();
            template.Status.ShouldBe(TemplateStatus.Disabled);

            TemplateVersion stored = await db.TemplateVersions
                .AsNoTracking()
                .WhereTemplateKey(TemplateKey.Trusted(key))
                .SingleAsync(candidate => candidate.Version == version);
            stored.Status.ShouldBe(TemplateVersionStatus.Draft);
        });
    }

    private TemplateManagementDbContext CreateDbContext(CompetingWriteInterceptor interceptor)
    {
        DbContextOptions<TemplateManagementDbContext> options =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(fixture.PostgresConnectionString)
                .AddInterceptors(interceptor)
                .Options;
        return new TemplateManagementDbContext(options);
    }

    private static readonly string[] RequiredOrderId = ["orderId"];

    private static TemplateVersionAnalyzer Analyzer()
        => new(new ScribanTemplateEngine(Options.Create(new TemplatingOptions()), new ScribanParseCache()));

    /// <summary>Second publishable draft on an existing template, mirroring the shared helper.</summary>
    private static async Task<int> CreatePublishableDraftOnAsync(HttpClient client, string key)
    {
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado novamente.</p>",
            bodyText = "Pedido {{ orderId }} atualizado novamente.",
        }, etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { orderId = new { type = "string" } },
            required = RequiredOrderId,
        }, etag);
        return version;
    }

    /// <summary>
    /// Runs the competing HTTP operation to completion right before the first
    /// write batch matching the marker executes, then lets the intercepted
    /// save proceed against the now-stale snapshot.
    /// </summary>
    private sealed class CompetingWriteInterceptor(
        string verb,
        string batchMarker,
        Func<Task<HttpResponseMessage>> competingAction) : DbCommandInterceptor
    {
        private int _fired;

        public HttpResponseMessage? CompetingResponse { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await RunCompetingActionAsync(command);
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await RunCompetingActionAsync(command);
            return await base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        private async Task RunCompetingActionAsync(DbCommand command)
        {
            var isTargetBatch = command.CommandText.Contains(verb, StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(batchMarker, StringComparison.Ordinal);
            if (!isTargetBatch || Interlocked.Exchange(ref _fired, 1) == 1)
            {
                return;
            }

            CompetingResponse = await competingAction();
        }
    }
}
