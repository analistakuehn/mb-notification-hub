using System.Net;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class ValidateTemplateVersionEndpointTests(TemplateManagementApiFixture fixture)
{
    private static readonly string[] RequiredOrderId = ["orderId"];

    [RequiresDockerFact]
    public async Task A_clean_draft_returns_200_with_a_fully_passed_report()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(
            client, TemplateApi.NewKey(), defaultLocale: "pt-BR", linkDomainsAllowed: ["montebravo.com.br"]);
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

        HttpResponseMessage response = await client.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("passed").GetBoolean().ShouldBeTrue();
        List<string> statuses = [.. body.GetProperty("checks").EnumerateArray()
            .Select(check => check.GetProperty("status").GetString()!)];
        statuses.ShouldNotBeEmpty();
        statuses.ShouldAllBe(status => status == "passed");
    }

    [RequiresDockerFact]
    public async Task Failing_checks_come_back_as_a_200_report_not_as_an_error()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        await TemplateApi.PutContentAsync(client, key, version, "email/pt-BR", new
        {
            subject = "Oi",
            body = "<p>Olá {{ nome }}</p>",
        }, etag);

        HttpResponseMessage response = await client.PostAsync(
            $"/v1/templates/{key}/versions/{version}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("passed").GetBoolean().ShouldBeFalse();
        List<JsonElement> checks = [.. body.GetProperty("checks").EnumerateArray()];

        JsonElement undeclared = checks.Single(check =>
            check.GetProperty("name").GetString() == "variables-declared"
            && check.GetProperty("status").GetString() == "failed");
        undeclared.GetProperty("message").GetString()!.ShouldContain("'nome'");
        undeclared.GetProperty("location").GetString().ShouldBe("email/pt-BR/body");

        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "channel-limits"
            && check.GetProperty("status").GetString() == "failed");
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "default-locale"
            && check.GetProperty("status").GetString() == "failed");
    }

    [RequiresDockerFact]
    public async Task An_unknown_version_returns_404()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.PostAsync(
            $"/v1/templates/{key}/versions/99/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-version-not-found");
    }

    [RequiresDockerFact]
    public async Task An_unknown_template_returns_404()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");

        HttpResponseMessage response = await client.PostAsync(
            $"/v1/templates/{TemplateApi.NewKey()}/versions/1/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-not-found");
    }

    [RequiresDockerFact]
    public async Task Without_a_bearer_token_the_endpoint_returns_401()
    {
        HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            "/v1/templates/any.key/versions/1/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }
}
