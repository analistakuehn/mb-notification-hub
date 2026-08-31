using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// What the historical read hands a caller reconstructing a past notification.
/// The lifecycle runs draft, published, superseded and never back, and only a
/// published version renders, so a version that is a draft today shipped
/// nothing and belongs outside this answer. Withholding it costs something,
/// though: the consumer turns the failure into a missing template block, which
/// reads exactly like a version that never existed. These tests hold both
/// halves, the answer and the witness that keeps the difference audible.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class HistoricalCatalogContractTests(TemplateManagementApiFixture fixture)
{
    private const string Application = "araia-cambio";

    private static readonly string[] RequiredOrderId = ["orderId"];

    [RequiresDockerFact]
    public async Task A_version_that_never_left_draft_is_withheld_and_named_in_the_log()
    {
        var recorded = new RecordingLoggerProvider();
        using WebApplicationFactory<Program> observed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(recorded)));
        HttpClient author = fixture.CreateAuthorClient("author-hist-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        Result<HistoricalTemplateVersion> found = await FindAsync(observed.Services, key, version);

        found.IsFailure.ShouldBeTrue();
        found.ErrorKind.ShouldBe(ResultErrorKind.NotFound);

        // Byte for byte the answer a version the store never had would get. The
        // caller cannot tell the two apart, and that is the cost the witness
        // below exists to pay for.
        found.Error.ShouldBe(DomainError.Format(
            ErrorCodes.TemplateVersionNotFound,
            $"Template '{key}' has no version {version}."));

        RecordedEvent witness = recorded.Events.Single(entry => entry.EventId.Id == 2120);
        witness.Level.ShouldBe(LogLevel.Error);
        witness.Message.ShouldContain(key);
        witness.Message.ShouldContain(TemplateVersionStatuses.Draft);
        witness.Message.ShouldContain(Application);
    }

    [RequiresDockerFact]
    public async Task A_published_version_and_the_one_it_superseded_both_answer()
    {
        HttpClient author = fixture.CreateAuthorClient("author-hist-2");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-hist-2");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, first);
        var second = await CreateClonedDraftAsync(author, key, first);
        await TemplateApi.PublishAsync(publisher, key, second);

        Result<HistoricalTemplateVersion> superseded = await FindAsync(fixture.Services, key, first);
        Result<HistoricalTemplateVersion> published = await FindAsync(fixture.Services, key, second);

        superseded.IsSuccess.ShouldBeTrue(superseded.Error);
        superseded.Value!.Version.ShouldBe(first);
        superseded.Value!.VersionStatus.ShouldBe(TemplateVersionStatuses.Superseded);

        published.IsSuccess.ShouldBeTrue(published.Error);
        published.Value!.Version.ShouldBe(second);
        published.Value!.VersionStatus.ShouldBe(TemplateVersionStatuses.Published);
    }

    /// <summary>
    /// The pin is read on its own axis, so the version still answers while its
    /// layout does not. The state is arranged behind the domain's back on
    /// purpose: publishing this version required the pin to resolve to a
    /// published layout, which is exactly why a draft here has to be reported
    /// instead of absorbed.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_pinned_layout_that_never_left_draft_is_omitted_and_named_in_the_log()
    {
        var recorded = new RecordingLoggerProvider();
        using WebApplicationFactory<Program> observed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(recorded)));
        HttpClient author = fixture.CreateAuthorClient("author-hist-3");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-hist-3");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (var key, var version) = await CreatePinnedPublishedVersionAsync(
            author, publisher, layoutKey, layoutVersion);

        Result<HistoricalTemplateVersion> before = await FindAsync(observed.Services, key, version);
        before.IsSuccess.ShouldBeTrue(before.Error);
        before.Value!.Layout.ShouldNotBeNull().Version.ShouldBe(layoutVersion);
        before.Value!.LayoutPin.ShouldNotBeNull().Version.ShouldBe(layoutVersion);
        recorded.Events.Count(entry => entry.EventId.Id == 3100).ShouldBe(0);

        await PushLayoutVersionBackToDraftAsync(layoutKey, layoutVersion);

        Result<HistoricalTemplateVersion> after = await FindAsync(observed.Services, key, version);

        after.IsSuccess.ShouldBeTrue(after.Error);
        after.Value!.Version.ShouldBe(version);
        after.Value!.Layout.ShouldBeNull();

        // The pin survives the withholding, and that is the whole difference
        // between this answer and the answer for a version that framed its
        // message with nothing.
        after.Value!.LayoutPin.ShouldNotBeNull().LayoutKey.ShouldBe(layoutKey);
        after.Value!.LayoutPin.Version.ShouldBe(layoutVersion);

        RecordedEvent witness = recorded.Events.Single(entry => entry.EventId.Id == 3100);
        witness.Level.ShouldBe(LogLevel.Error);
        witness.Message.ShouldContain(layoutKey);
        witness.Message.ShouldContain(key);
        witness.Message.ShouldContain(LayoutVersionStatuses.Draft);
    }

    /// <summary>
    /// The one legitimate absence on this axis, asserted as an absence of both
    /// members and of any witness. It is the control the two anomaly cases are
    /// read against: if this answer carried a pin, the pin would stop meaning
    /// that the message was framed at all.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_version_that_pinned_no_layout_declares_no_pin_and_no_witness()
    {
        var recorded = new RecordingLoggerProvider();
        using WebApplicationFactory<Program> observed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(recorded)));
        HttpClient author = fixture.CreateAuthorClient("author-hist-4");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-hist-4");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, version);

        Result<HistoricalTemplateVersion> found = await FindAsync(observed.Services, key, version);

        found.IsSuccess.ShouldBeTrue(found.Error);
        found.Value!.LayoutPin.ShouldBeNull();
        found.Value!.Layout.ShouldBeNull();
        recorded.Events.Count(entry => entry.EventId.Id is 3100 or 3101).ShouldBe(0);
    }

    /// <summary>
    /// The second anomaly, and the one the answer used to be silent about in
    /// every way. Publishing this version required the pin to resolve to a
    /// published layout version, and no route of this module deletes or moves a
    /// layout version afterwards, so a pin that stops resolving was moved from
    /// outside the module. The answer keeps the pin and drops the layout, and
    /// the log is what names which of the two withholdings happened.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_pin_that_no_longer_resolves_is_declared_and_named_in_the_log()
    {
        var recorded = new RecordingLoggerProvider();
        using WebApplicationFactory<Program> observed = fixture.WithWebHostBuilder(builder =>
            builder.ConfigureLogging(logging => logging.AddProvider(recorded)));
        HttpClient author = fixture.CreateAuthorClient("author-hist-5");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-hist-5");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (var key, var version) = await CreatePinnedPublishedVersionAsync(
            author, publisher, layoutKey, layoutVersion);
        var unresolvable = layoutVersion + 100;

        await RepointPinAsync(key, version, unresolvable);
        Result<HistoricalTemplateVersion> found = await FindAsync(observed.Services, key, version);

        found.IsSuccess.ShouldBeTrue(found.Error);
        found.Value!.Layout.ShouldBeNull();

        // The pin as the version declares it, not as the store can honour it:
        // the number that resolves to nothing is exactly the fact an auditor
        // needs, and inventing a resolvable one would hide the anomaly.
        found.Value!.LayoutPin.ShouldNotBeNull().LayoutKey.ShouldBe(layoutKey);
        found.Value!.LayoutPin.Version.ShouldBe(unresolvable);

        RecordedEvent witness = recorded.Events.Single(entry => entry.EventId.Id == 3101);
        witness.Level.ShouldBe(LogLevel.Error);
        witness.Message.ShouldContain(layoutKey);
        witness.Message.ShouldContain(key);
        witness.Message.ShouldContain(unresolvable.ToString(CultureInfo.InvariantCulture));
        recorded.Events.Count(entry => entry.EventId.Id == 3100).ShouldBe(0);
    }

    private static async Task<Result<HistoricalTemplateVersion>> FindAsync(
        IServiceProvider services,
        string key,
        int version)
    {
        using IServiceScope scope = services.CreateScope();
        IHistoricalCatalog catalog = scope.ServiceProvider.GetRequiredService<IHistoricalCatalog>();
        return await catalog.FindTemplateVersionAsync(Application, key, version, CancellationToken.None);
    }

    private static async Task<int> CreateClonedDraftAsync(HttpClient author, string key, int fromVersion)
    {
        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions", new { fromVersion });
        response.EnsureSuccessStatusCode();
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        return body.GetProperty("version").GetInt32();
    }

    private static async Task<(string Key, int Version)> CreatePinnedPublishedVersionAsync(
        HttpClient author,
        HttpClient publisher,
        string layoutKey,
        int layoutVersion)
    {
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        etag = await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new { orderId = new { type = "string" } },
            required = RequiredOrderId,
        }, etag);
        HttpResponseMessage pinned = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));
        pinned.EnsureSuccessStatusCode();
        await TemplateApi.PublishAsync(publisher, key, version);
        return (key, version);
    }

    /// <summary>
    /// Moves the pin of a published version onto a layout version number that
    /// was never created. No route of this module edits a published version, so
    /// the row is edited directly, which is the only way this state is reached
    /// at all.
    /// </summary>
    private Task RepointPinAsync(string templateKey, int version, int layoutVersion)
        => fixture.ExecuteDbAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE templatemanagement.template_version
            SET layout_version = {layoutVersion}
            WHERE template_key = {templateKey} AND version = {version}
            """));

    /// <summary>
    /// Writes the status the lifecycle refuses to write. Nothing in the domain
    /// walks a published version back to draft, so the row is edited directly:
    /// the read has to hold even for a store somebody else moved.
    /// </summary>
    private Task PushLayoutVersionBackToDraftAsync(string layoutKey, int layoutVersion)
        => fixture.ExecuteDbAsync(db => db.Database.ExecuteSqlAsync($"""
            UPDATE templatemanagement.layout_version
            SET status = {LayoutVersionStatuses.Draft}
            WHERE layout_key = {layoutKey} AND version = {layoutVersion}
            """));
}
