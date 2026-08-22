using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class TemplateLayoutIntegrationEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Pinning_a_layout_on_a_draft_requires_the_current_entity_tag_and_changes_the_content_hash()
    {
        var author = fixture.CreateAuthorClient("author-tl-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        HttpResponseMessage current = await author.GetAsync($"/v1/templates/{key}/versions/{version}");
        var etag = current.Headers.ETag!.ToString();
        var hashBefore = (await TemplateApi.ReadJsonAsync(current)).GetProperty("contentHash").GetString();

        HttpResponseMessage stale = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey = "email.base", layoutVersion = 1 },
            ifMatch: "\"etag-antigo\""));
        HttpResponseMessage response = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey = "email.base", layoutVersion = 1 },
            etag));

        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("layoutKey").GetString().ShouldBe("email.base");
        body.GetProperty("layoutVersion").GetInt32().ShouldBe(1);
        body.GetProperty("contentHash").GetString().ShouldNotBe(hashBefore);
    }

    [RequiresDockerFact]
    public async Task A_partial_layout_reference_returns_400()
    {
        var author = fixture.CreateAuthorClient("author-tl-2");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        HttpResponseMessage current = await author.GetAsync($"/v1/templates/{key}/versions/{version}");

        HttpResponseMessage response = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey = "email.base" },
            current.Headers.ETag!.ToString()));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task Referencing_a_layout_without_a_published_version_fails_the_layout_reference_check_and_blocks_publish()
    {
        var author = fixture.CreateAuthorClient("author-tl-3");
        var publisher = fixture.CreatePublisherClient("publisher-tl-3");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(author);
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);

        HttpResponseMessage validation = await author.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);
        HttpResponseMessage publish = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        validation.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement report = await TemplateApi.ReadJsonAsync(validation);
        report.GetProperty("passed").GetBoolean().ShouldBeFalse();
        report.GetProperty("checks").EnumerateArray().ShouldContain(check =>
            check.GetProperty("name").GetString() == "layout-reference"
            && check.GetProperty("status").GetString() == "failed");
        publish.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await TemplateApi.ReadJsonAsync(publish);
        problem.GetProperty("type").GetString().ShouldBe("template-validation-failed");
    }

    [RequiresDockerFact]
    public async Task A_template_pinned_to_a_published_layout_validates_and_publishes()
    {
        var author = fixture.CreateAuthorClient("author-tl-4");
        var publisher = fixture.CreatePublisherClient("publisher-tl-4");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);

        HttpResponseMessage validation = await author.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);
        HttpResponseMessage publish = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        JsonElement report = await TemplateApi.ReadJsonAsync(validation);
        report.GetProperty("passed").GetBoolean().ShouldBeTrue();
        report.GetProperty("checks").EnumerateArray().ShouldContain(check =>
            check.GetProperty("name").GetString() == "layout-reference"
            && check.GetProperty("status").GetString() == "passed");
        publish.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [RequiresDockerFact]
    public async Task Rendering_a_template_with_a_published_layout_wraps_body_and_text_exactly()
    {
        var author = fixture.CreateAuthorClient("author-tl-5");
        var publisher = fixture.CreatePublisherClient("publisher-tl-5");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);

        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "email", locale = "pt-BR", variables = new { orderId = "42" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("subject").GetString().ShouldBe("Pedido 42");
        body.GetProperty("body").GetString().ShouldBe(
            "<html><header>MB</header><p>Pedido 42 atualizado.</p><footer>rodapé</footer></html>");
        body.GetProperty("bodyText").GetString().ShouldBe("MB\nPedido 42 atualizado.\nrodapé");
    }

    [RequiresDockerFact]
    public async Task Without_a_layout_reference_the_render_stays_unwrapped()
    {
        var author = fixture.CreateAuthorClient("author-tl-6");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "email", locale = "pt-BR", variables = new { orderId = "42" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("body").GetString().ShouldBe("<p>Pedido 42 atualizado.</p>");
    }

    [RequiresDockerFact]
    public async Task Rendering_a_channel_the_layout_does_not_cover_returns_404_layout_content_not_found()
    {
        var author = fixture.CreateAuthorClient("author-tl-7");
        var publisher = fixture.CreatePublisherClient("publisher-tl-7");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        etag = await TemplateApi.PutContentAsync(author, key, version, "sms/pt-BR", new
        {
            body = "Código {{ code }}",
        }, etag);
        await author.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            etag));

        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "pt-BR", variables = new { code = "998877" } });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("layout-content-not-found");
    }

    private static async Task PinLayoutAsync(
        HttpClient client,
        string key,
        int version,
        string layoutKey,
        int layoutVersion)
    {
        HttpResponseMessage current = await client.GetAsync($"/v1/templates/{key}/versions/{version}");
        current.EnsureSuccessStatusCode();
        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/layout",
            new { layoutKey, layoutVersion },
            current.Headers.ETag!.ToString()));
        response.EnsureSuccessStatusCode();
    }
}
