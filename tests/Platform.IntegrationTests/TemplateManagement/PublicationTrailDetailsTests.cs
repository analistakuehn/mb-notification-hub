using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// What the trail keeps about a publication that passed. A passed report still
/// carries warnings, and the message of a warning interpolates a name taken
/// out of the content being published, which is the one thing an append-only
/// row must not hold.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PublicationTrailDetailsTests(TemplateManagementApiFixture fixture)
{
    /// <summary>
    /// A token that can only reach the trail through the message of a check,
    /// because no other field of the publication carries it.
    /// </summary>
    private const string LeakProbe = "zxqvortex";

    [RequiresDockerFact]
    public async Task Publishing_and_rolling_back_the_same_template_record_the_same_details_shape()
    {
        HttpClient author = fixture.CreateAuthorClient("author-ptd-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-ptd-1");
        HttpClient roller = fixture.CreatePublisherClient("publisher-ptd-2");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, first);
        var second = await CreateEditedDraftAsync(author, key, first);
        await TemplateApi.PublishAsync(publisher, key, second);

        HttpResponseMessage response = await roller.PostAsJsonAsync(
            $"/v1/templates/{key}/rollback", new { toVersion = first });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var third = (await TemplateApi.ReadJsonAsync(response)).GetProperty("version").GetInt32();
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            JsonElement publication = await DetailsOfAsync(db, "template.version.published", $"{key}:{second}");
            JsonElement rollback = await DetailsOfAsync(db, "template.rollback", $"{key}:{third}");

            Names(rollback).ShouldBe(
                [.. Names(publication), "publishedVersion", "rolledBackFrom"],
                ignoreOrder: true);
            Names(publication.GetProperty("validation")).ShouldBe(
                Names(rollback.GetProperty("validation")),
                ignoreOrder: true);
            Names(rollback.GetProperty("validation")).ShouldBe(
                ["checks", "passed", "warned", "warnings"],
                ignoreOrder: true);
        });
    }

    [RequiresDockerFact]
    public async Task A_warning_that_names_an_unused_variable_never_reaches_the_publication_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-ptd-3");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-ptd-3");
        (var key, var version) = await CreateDraftWarningAboutAnUnusedVariableAsync(author);

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await SingleAsync(db, "template.version.published", $"{key}:{version}");
            audit.DetailsJson.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
                "the warning names a declared variable and that name comes from the content");
            audit.Canonical!.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
                "the canonical text is the payload the hash covers and nobody can rewrite it");
        });
    }

    [RequiresDockerFact]
    public async Task A_layout_warning_that_names_a_link_host_never_reaches_the_publication_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-ptd-4");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-ptd-4");
        (var key, var version) = await LayoutApi.CreatePublishableDraftAsync(
            author,
            body: "<html><header>MB</header>"
                + $"<a href=\"https://{LeakProbe}.example.com/promo\">Ver</a>"
                + "{{ content }}<footer>rodape</footer></html>");

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/layouts/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await SingleAsync(db, "layout.version.published", $"{key}:{version}");
            audit.DetailsJson.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
                "the warning names a link host lifted out of the wrapper body");
            audit.Canonical!.Contains(LeakProbe, StringComparison.Ordinal).ShouldBeFalse(
                "the canonical text is the payload the hash covers and nobody can rewrite it");
            Values(Parse(audit.DetailsJson).GetProperty("validation"), "warned")
                .ShouldContain("url-allowlist");
        });
    }

    [RequiresDockerFact]
    public async Task A_publication_whose_report_carries_a_warning_still_lands_in_the_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-ptd-5");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-ptd-5");
        (var key, var version) = await CreateDraftWarningAboutAnUnusedVariableAsync(author);

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await SingleAsync(db, "template.version.published", $"{key}:{version}");
            JsonElement validation = Parse(audit.DetailsJson).GetProperty("validation");
            validation.GetProperty("passed").GetBoolean().ShouldBeTrue();
            validation.GetProperty("warnings").GetInt32().ShouldBe(1);
            Values(validation, "warned").ShouldContain("variables-used");
            Values(validation, "checks").ShouldContain("variables-used");
        });
    }

    /// <summary>
    /// A draft that publishes and warns: the schema declares a variable no
    /// content reads, which the catalog reports as a warning and never as a
    /// refusal.
    /// </summary>
    private static async Task<(string Key, int Version)> CreateDraftWarningAboutAnUnusedVariableAsync(
        HttpClient author)
    {
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, version, new
        {
            type = "object",
            properties = new Dictionary<string, object>
            {
                ["orderId"] = new { type = "string" },
                [LeakProbe] = new { type = "string" },
            },
        }, etag);
        return (key, version);
    }

    private static async Task<int> CreateEditedDraftAsync(HttpClient author, string key, int fromVersion)
    {
        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions", new { fromVersion });
        response.EnsureSuccessStatusCode();
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        var version = body.GetProperty("version").GetInt32();
        var etag = response.Headers.ETag!.ToString();
        await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} foi atualizado agora.</p>",
            bodyText = "Pedido {{ orderId }} foi atualizado agora.",
        }, etag);
        return version;
    }

    private static async Task<AuditEvent> SingleAsync(AuditDbContext db, string action, string entityId)
        => await db.AuditEvents.AsNoTracking()
            .SingleAsync(candidate => candidate.Action == action && candidate.EntityId == entityId);

    private static async Task<JsonElement> DetailsOfAsync(AuditDbContext db, string action, string entityId)
        => Parse((await SingleAsync(db, action, entityId)).DetailsJson);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static List<string> Names(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name)];

    private static List<string> Values(JsonElement element, string property)
        => [.. element.GetProperty(property).EnumerateArray().Select(entry => entry.GetString()!)];
}
