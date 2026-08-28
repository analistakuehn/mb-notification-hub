using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// The published read contracts that need the identity together with its
/// published version read it through one loader, so the pair of round trips it
/// costs is paid once per notification instead of once per contract.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PublishedContextSharingTests(TemplateManagementApiFixture fixture)
{
    private const string Application = "araia-cambio";

    [RequiresDockerFact]
    public async Task One_load_of_the_published_context_serves_the_validator_and_the_renderer()
    {
        var key = await PublishTemplateAsync();
        var commands = new CommandCounter();
        await using TemplateManagementDbContext store = CountingStore(commands);
        using var cache = new PublishedReadCache(TimeProvider.System);
        var loader = new PublishedContextLoader(store, cache);

        Result<VariablesValidationReport> report = await new PublishedVariablesValidator(loader)
            .ValidateAsync(Application, key, Variables("""{ "orderId": "42" }"""), CancellationToken.None);
        var afterValidation = commands.Executed;

        Result<PublishedTemplateRender> rendered = await RendererOver(store, cache, loader)
            .RenderAsync(RenderRequestFor(key), CancellationToken.None);

        report.IsSuccess.ShouldBeTrue(report.Error);
        rendered.IsSuccess.ShouldBeTrue(rendered.Error);
        rendered.Value!.Full.Body.ShouldBe("<p>Pedido 42 atualizado.</p>");

        // The identity and its published version: two round trips, and the
        // render that follows adds none because the context is already in hand.
        afterValidation.ShouldBe(2);
        commands.Executed.ShouldBe(2);
        cache.PointerLoads.ShouldBe(1);
        cache.PointerHits.ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task The_report_read_from_memory_is_the_report_the_store_produces()
    {
        var key = await PublishTemplateAsync();
        JsonElement variables = Variables("""{ "cupom": "MB10" }""");
        var commands = new CommandCounter();
        await using TemplateManagementDbContext store = CountingStore(commands);
        using var cache = new PublishedReadCache(TimeProvider.System);
        var validator = new PublishedVariablesValidator(new PublishedContextLoader(store, cache));

        Result<VariablesValidationReport> fromStore = await validator.ValidateAsync(
            Application, key, variables, CancellationToken.None);
        var afterStore = commands.Executed;
        Result<VariablesValidationReport> fromMemory = await validator.ValidateAsync(
            Application, key, variables, CancellationToken.None);

        fromStore.IsSuccess.ShouldBeTrue(fromStore.Error);
        fromMemory.IsSuccess.ShouldBeTrue(fromMemory.Error);

        // A payload the published schema never declared, so both reports have
        // something to say and saying nothing would not pass for agreement.
        fromStore.Value!.Passed.ShouldBeFalse();
        Checks(fromStore.Value!).ShouldContain(
            (ValidationCheckNames.VariablesDeclared, VariablesValidationStatuses.Failed));
        Checks(fromMemory.Value!).ShouldBe(Checks(fromStore.Value!));
        fromMemory.Value!.Checks.Select(check => check.Message)
            .ShouldBe(fromStore.Value!.Checks.Select(check => check.Message));
        commands.Executed.ShouldBe(afterStore);
    }

    [RequiresDockerFact]
    public async Task The_validator_reports_a_template_the_application_does_not_own_as_not_found()
    {
        var key = await PublishTemplateAsync();

        Result<VariablesValidationReport> refused =
            await ValidateThroughTheHostAsync("araia-investimentos", key);

        refused.IsFailure.ShouldBeTrue();
        refused.ErrorKind.ShouldBe(ResultErrorKind.NotFound);
        refused.Error.ShouldBe(DomainError.Format(
            ErrorCodes.TemplateNotFound,
            $"Application 'araia-investimentos' has no template '{key}'."));
    }

    [RequiresDockerFact]
    public async Task The_validator_refuses_a_deprecated_template_as_a_business_rule_violation()
    {
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        var key = await PublishTemplateAsync();
        HttpResponseMessage deprecated = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate",
            new { reason = "superseded-by-new-version", note = "substituído pela nova jornada" });
        deprecated.EnsureSuccessStatusCode();

        Result<VariablesValidationReport> refused = await ValidateThroughTheHostAsync(Application, key);

        refused.IsFailure.ShouldBeTrue();
        refused.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        refused.Error.ShouldBe(DomainError.Format(
            TemplateRejectionReasons.Deprecated,
            $"Template '{key}' is deprecated and rejects new notification requests."));
    }

    /// <summary>Through the host, which is also where the composed loader has to resolve.</summary>
    private async Task<Result<VariablesValidationReport>> ValidateThroughTheHostAsync(
        string application,
        string key)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedVariablesValidator validator =
            scope.ServiceProvider.GetRequiredService<IPublishedVariablesValidator>();
        return await validator.ValidateAsync(
            application, key, Variables("""{ "orderId": "42" }"""), CancellationToken.None);
    }

    private async Task<string> PublishTemplateAsync()
    {
        HttpClient author = fixture.CreateAuthorClient("author-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);
        return key;
    }

    private TemplateManagementDbContext CountingStore(CommandCounter counter)
        => new(new DbContextOptionsBuilder<TemplateManagementDbContext>()
            .UseNpgsql(fixture.PostgresConnectionString)
            .AddInterceptors(counter)
            .Options);

    private static PublishedTemplateRenderer RendererOver(
        TemplateManagementDbContext store,
        PublishedReadCache cache,
        PublishedContextLoader loader)
        => new(
            store,
            new ScribanTemplateEngine(Options.Create(new TemplatingOptions()), new ScribanParseCache()),
            cache,
            loader,
            NullLogger<PublishedTemplateRenderer>.Instance);

    private static PublishedRenderRequest RenderRequestFor(string key) => new()
    {
        Application = Application,
        TemplateKey = key,
        Channel = "email",
        Locale = "pt-BR",
        Variables = Variables("""{ "orderId": "42" }"""),
    };

    private static List<(string Name, string Status)> Checks(VariablesValidationReport report)
        => [.. report.Checks.Select(check => (check.Name, check.Status))];

    private static JsonElement Variables(string json)
        => JsonSerializer.Deserialize<JsonElement>(json);

    /// <summary>
    /// Counts the round trips a published read costs, which is the quantity the
    /// shared memoization exists to remove.
    /// </summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        private int _executed;

        internal int Executed => Volatile.Read(ref _executed);

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _executed);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _executed);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
