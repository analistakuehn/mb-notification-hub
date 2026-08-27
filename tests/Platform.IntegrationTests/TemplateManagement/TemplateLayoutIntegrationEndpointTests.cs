using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class TemplateLayoutIntegrationEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Pinning_a_layout_on_a_draft_requires_the_current_entity_tag_and_changes_the_content_hash()
    {
        HttpClient author = fixture.CreateAuthorClient("author-tl-1");
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
        HttpClient author = fixture.CreateAuthorClient("author-tl-2");
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
        HttpClient author = fixture.CreateAuthorClient("author-tl-3");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-3");
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
    public async Task Pinning_a_disabled_layout_fails_the_layout_reference_check_and_blocks_publish()
    {
        // The gate is where this has to bite. Without it the author publishes
        // on a clean report and the pin only fails at dispatch, one refused
        // notification at a time.
        HttpClient author = fixture.CreateAuthorClient("author-tl-9");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-9");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/disable", new { reason = "conteúdo comprometido no wrapper" }))
            .EnsureSuccessStatusCode();
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);

        HttpResponseMessage validation = await author.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);
        HttpResponseMessage publish = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        JsonElement report = await TemplateApi.ReadJsonAsync(validation);
        report.GetProperty("passed").GetBoolean().ShouldBeFalse();
        report.GetProperty("checks").EnumerateArray().ShouldContain(check =>
            check.GetProperty("name").GetString() == "layout-reference"
            && check.GetProperty("status").GetString() == "failed"
            && check.GetProperty("message").GetString()!.Contains("disabled", StringComparison.Ordinal));
        publish.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [RequiresDockerFact]
    public async Task Pinning_a_deprecated_layout_fails_the_layout_reference_check_and_blocks_publish()
    {
        // Deprecation is exactly the statement that the layout takes no new
        // reference, and a version being published is a new reference. The
        // wording differs from the disabled refusal on purpose: one says the
        // layout is finished, the other says it is on its way out.
        HttpClient author = fixture.CreateAuthorClient("author-tl-10");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-10");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/deprecate", new { reason = "identidade visual antiga" }))
            .EnsureSuccessStatusCode();
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);

        HttpResponseMessage validation = await author.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);
        HttpResponseMessage publish = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        JsonElement report = await TemplateApi.ReadJsonAsync(validation);
        report.GetProperty("passed").GetBoolean().ShouldBeFalse();
        report.GetProperty("checks").EnumerateArray().ShouldContain(check =>
            check.GetProperty("name").GetString() == "layout-reference"
            && check.GetProperty("status").GetString() == "failed"
            && check.GetProperty("message").GetString()!.Contains("deprecated", StringComparison.Ordinal));
        publish.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [RequiresDockerFact]
    public async Task A_template_pinned_to_a_published_layout_validates_and_publishes()
    {
        HttpClient author = fixture.CreateAuthorClient("author-tl-4");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-4");
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
        HttpClient author = fixture.CreateAuthorClient("author-tl-5");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-5");
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
        HttpClient author = fixture.CreateAuthorClient("author-tl-6");
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
        HttpClient author = fixture.CreateAuthorClient("author-tl-7");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-7");
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

    [RequiresDockerFact]
    public async Task A_disabled_layout_refuses_the_authoring_render()
    {
        // The publication gate stops a version from pinning a layout already
        // out of service, and it says nothing about a version that pinned one
        // while it was alive. That version still previews, and a preview that
        // frames what the dispatch refuses reads to the author as proof that
        // everything is in order: worse than no preview at all. The refusal is
        // the diagnosis.
        HttpClient author = fixture.CreateAuthorClient("author-tl-12");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-12");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);
        (await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/disable", new { reason = "wrapper retirado de circulação" }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "email", locale = "pt-BR", variables = new { orderId = "42" } });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe(LayoutRejectionReasons.Disabled);
    }

    [RequiresDockerFact]
    public async Task A_deprecated_layout_still_frames_the_authoring_render()
    {
        // The falsification pair of the refusal above, for the same reason it
        // exists on the published side: without it that assertion would hold
        // for any status other than active, and deprecation is exactly the
        // status that must keep framing.
        HttpClient author = fixture.CreateAuthorClient("author-tl-13");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-13");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);
        (await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/deprecate", new { reason = "identidade visual antiga" }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "email", locale = "pt-BR", variables = new { orderId = "42" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("body").GetString().ShouldBe(
            "<html><header>MB</header><p>Pedido 42 atualizado.</p><footer>rodapé</footer></html>");
    }

    [RequiresDockerFact]
    public async Task Publishing_a_template_pinned_to_a_layout_with_a_foreign_host_is_blocked()
    {
        // The layout publishes on its own: it answers to no allowlist, because
        // it has no template until one pins it. The template that pins it is
        // where the allowed domains apply, and the wrapper is part of what it
        // sends.
        HttpClient author = fixture.CreateAuthorClient("author-tl-8");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-8");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            author,
            body: """<html><header>MB</header><a href="https://evil.example.io/x">promo</a>{{ content }}</html>""");
        await LayoutApi.PublishAsync(publisher, layoutKey, layoutVersion);
        (var key, var version) = await CreateDraftAllowingMonteBravoAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);

        HttpResponseMessage validation = await author.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);
        HttpResponseMessage publish = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        JsonElement report = await TemplateApi.ReadJsonAsync(validation);
        report.GetProperty("passed").GetBoolean().ShouldBeFalse();
        report.GetProperty("checks").EnumerateArray().ShouldContain(check =>
            check.GetProperty("name").GetString() == "url-allowlist"
            && check.GetProperty("status").GetString() == "failed"
            && check.GetProperty("message").GetString()!.Contains("evil.example.io", StringComparison.Ordinal));
        publish.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [RequiresDockerFact]
    public async Task A_disabled_layout_is_the_only_failure_the_report_carries()
    {
        // A pin the layout-reference check refuses stays out of every rule
        // that reads the layout text, so the report keeps naming one cause.
        // The wrapper here carries a host outside the allowlist, which is
        // exactly the finding that must not come back a second time: the
        // author is being told to drop this layout, not to negotiate its
        // links.
        HttpClient author = fixture.CreateAuthorClient("author-tl-11");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-11");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishableDraftAsync(
            author,
            body: """<html><header>MB</header><a href="https://evil.example.io/x">promo</a>{{ content }}</html>""");
        await LayoutApi.PublishAsync(publisher, layoutKey, layoutVersion);
        (await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/disable", new { reason = "wrapper fora de uso" }))
            .EnsureSuccessStatusCode();
        (var key, var version) = await CreateDraftAllowingMonteBravoAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);

        HttpResponseMessage validation = await author.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);

        JsonElement report = await TemplateApi.ReadJsonAsync(validation);
        List<string?> failed = report.GetProperty("checks").EnumerateArray()
            .Where(check => check.GetProperty("status").GetString() == "failed")
            .Select(check => check.GetProperty("name").GetString())
            .ToList();
        failed.Count.ShouldBe(1, $"achados reprovados: {string.Join(", ", failed)}");
        failed[0].ShouldBe("layout-reference");
    }

    [RequiresDockerFact]
    public async Task A_disabled_layout_stops_framing_in_the_process_that_disabled_it()
    {
        // The published render memoizes the layout identity for the pointer
        // window, so a render that ran before the disable is what hides the
        // refusal. The warm-up below is that render.
        HttpClient author = fixture.CreateAuthorClient("author-tl-14");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-14");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        var key = await PublishTemplatePinnedToLayoutAsync(author, publisher, layoutKey, layoutVersion);
        (await RenderPublishedEmailAsync(key)).IsSuccess.ShouldBeTrue();

        (await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/disable", new { reason = "wrapper retirado de circulação" }))
            .EnsureSuccessStatusCode();

        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        rendered.Error.ShouldBe(LayoutRejectionReasons.Disabled);
        rendered.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
    }

    [RequiresDockerFact]
    public async Task A_deprecated_layout_keeps_framing_after_the_deprecation_reaches_the_cache()
    {
        // Falsification pair of the refusal above: without it that assertion
        // holds for any status other than active, and deprecation is exactly
        // the status that must keep framing. The load counter is what
        // separates "still frames because the identity was read again" from
        // "still frames because nothing ever read it again".
        HttpClient author = fixture.CreateAuthorClient("author-tl-15");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-tl-15");
        (var layoutKey, var layoutVersion) = await LayoutApi.CreatePublishedLayoutAsync(author, publisher);
        var key = await PublishTemplatePinnedToLayoutAsync(author, publisher, layoutKey, layoutVersion);
        (await RenderPublishedEmailAsync(key)).IsSuccess.ShouldBeTrue();

        (await publisher.PostAsJsonAsync(
            $"/v1/layouts/{layoutKey}/deprecate", new { reason = "identidade visual antiga" }))
            .EnsureSuccessStatusCode();

        PublishedReadCache cache = fixture.Services.GetRequiredService<PublishedReadCache>();
        var loads = cache.PointerLoads;
        Result<PublishedTemplateRender> rendered = await RenderPublishedEmailAsync(key);

        // Exactly one: the layout identity comes back from the store, and the
        // published context of a template the deprecation never touched keeps
        // answering from memory.
        (cache.PointerLoads - loads).ShouldBe(
            1, "a depreciação precisa derrubar a identidade memorizada do layout, e só ela");
        rendered.Value!.Full.Body.ShouldBe(
            "<html><header>MB</header><p>Pedido 42 atualizado.</p><footer>rodapé</footer></html>");
    }

    /// <summary>A published template version whose email content is framed by the given layout version.</summary>
    private static async Task<string> PublishTemplatePinnedToLayoutAsync(
        HttpClient author,
        HttpClient publisher,
        string layoutKey,
        int layoutVersion)
    {
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        await PinLayoutAsync(author, key, version, layoutKey, layoutVersion);
        await TemplateApi.PublishAsync(publisher, key, version);
        return key;
    }

    private async Task<Result<PublishedTemplateRender>> RenderPublishedEmailAsync(string key)
    {
        using IServiceScope scope = fixture.Services.CreateScope();
        IPublishedTemplateRenderer renderer =
            scope.ServiceProvider.GetRequiredService<IPublishedTemplateRenderer>();
        return await renderer.RenderAsync(new PublishedRenderRequest
        {
            Application = "araia-cambio",
            TemplateKey = key,
            Channel = "email",
            Locale = "pt-BR",
            Variables = JsonSerializer.Deserialize<JsonElement>("""{ "orderId": "42" }"""),
        }, CancellationToken.None);
    }

    private static readonly string[] MonteBravoDomain = ["montebravo.com.br"];

    private static readonly string[] RequiredOrderId = ["orderId"];

    private static async Task<(string Key, int Version)> CreateDraftAllowingMonteBravoAsync(HttpClient client)
    {
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: MonteBravoDomain);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} atualizado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { orderId = new { type = "string" } },
            required = RequiredOrderId,
        }, etag);
        return (key, version);
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
