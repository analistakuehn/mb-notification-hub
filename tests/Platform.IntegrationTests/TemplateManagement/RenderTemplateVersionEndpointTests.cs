using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class RenderTemplateVersionEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Rendering_an_email_draft_returns_the_substituted_fields()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} de {{ user.name }}.</p>",
            bodyText = "Pedido {{ orderId }} de {{ user.name }}.",
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { orderId = "42", user = new { name = "Ana" } },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("channel").GetString().ShouldBe("email");
        body.GetProperty("requestedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("resolvedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("subject").GetString().ShouldBe("Pedido 42");
        body.GetProperty("body").GetString().ShouldBe("<p>Pedido 42 de Ana.</p>");
        body.GetProperty("bodyText").GetString().ShouldBe("Pedido 42 de Ana.");
    }

    [RequiresDockerFact]
    public async Task An_oversized_variables_payload_returns_400_before_reaching_the_engine()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");

        // No template setup on purpose: validation must reject the payload
        // before the handler touches the catalog or the engine.
        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{TemplateApi.NewKey()}/versions/1/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { blob = new string('x', 300_000) },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task A_regional_locale_falls_back_to_its_base_language_content()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "en");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt", new
        {
            body = "Código {{ code }}",
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "pt-BR", variables = new { code = "998877" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("requestedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("resolvedLocale").GetString().ShouldBe("pt");
        body.GetProperty("body").GetString().ShouldBe("Código 998877");
    }

    [RequiresDockerFact]
    public async Task An_unmatched_locale_falls_back_to_the_template_default()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt-BR", new
        {
            body = "Código {{ code }}",
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "en-US", variables = new { code = "112233" } });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("resolvedLocale").GetString().ShouldBe("pt-BR");
        body.GetProperty("body").GetString().ShouldBe("Código 112233");
    }

    [RequiresDockerFact]
    public async Task A_locale_that_resolves_nowhere_returns_404_content_not_found()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt", new { body = "Código {{ code }}" }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "en-US" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-content-not-found");
    }

    [RequiresDockerFact]
    public async Task A_channel_without_content_returns_404_content_not_found()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt-BR", new { body = "Código {{ code }}" }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "push", locale = "pt-BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-content-not-found");
    }

    [RequiresDockerFact]
    public async Task An_unknown_version_returns_404()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/99/render",
            new { channel = "sms", locale = "pt-BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-version-not-found");
    }

    [RequiresDockerFact]
    public async Task A_url_variable_outside_the_allowlist_returns_400_problem_details()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Acesso",
            body = "<p>Acesse {{ portalUrl }}</p>",
            bodyText = "Acesse {{ portalUrl }}",
        }, etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { portalUrl = new { type = "string", format = "url" } },
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { portalUrl = "https://phishing.example.io/login" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        problem.GetProperty("detail").GetString()!.ShouldContain("'portalUrl'");
        problem.GetProperty("detail").GetString()!.ShouldNotContain("phishing.example.io");
    }

    [RequiresDockerFact]
    public async Task A_string_variable_carrying_a_foreign_host_returns_400_problem_details()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        etag = await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Acesso",
            body = "<p>Acesse {{ link }}</p>",
            bodyText = "Acesse {{ link }}",
        }, etag);
        await TemplateApi.PutSchemaAsync(client, key, version, new
        {
            type = "object",
            properties = new { link = new { type = "string" } },
        }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new
            {
                channel = "email",
                locale = "pt-BR",
                variables = new { link = "https://evil.example.io/pay?token=abc" },
            });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("url-domain-not-allowed");
        problem.GetProperty("detail").GetString()!.ShouldContain("evil.example.io");
        problem.GetProperty("detail").GetString()!.ShouldNotContain("token");
    }

    [RequiresDockerFact]
    public async Task A_missing_variable_returns_400_render_failed()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "sms/pt-BR", new { body = "Olá {{ nome }}" }, etag);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions/{version}/render",
            new { channel = "sms", locale = "pt-BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-render-failed");
        problem.GetProperty("detail").GetString()!.ShouldContain("nome");
    }
}
