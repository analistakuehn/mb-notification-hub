using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class CreateTemplateEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Creating_a_template_returns_201_with_its_location_and_metadata()
    {
        var client = fixture.CreateAuthorClient("author-1");
        var key = TemplateApi.NewKey();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/templates",
            TemplateApi.TemplateBody(key, application: "araia-cambio", @class: "critical"));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe($"/v1/templates/{key}");
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("key").GetString().ShouldBe(key);
        body.GetProperty("application").GetString().ShouldBe("araia-cambio");
        body.GetProperty("class").GetString().ShouldBe("critical");
        body.GetProperty("status").GetString().ShouldBe("active");
        body.GetProperty("ownerTeam").GetString().ShouldBe("growth-squad");
        body.GetProperty("legalBasis").GetString().ShouldBe("execucao-de-contrato");
    }

    [RequiresDockerFact]
    public async Task Reusing_a_template_key_returns_409_template_already_exists()
    {
        var client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.PostAsJsonAsync("/v1/templates", TemplateApi.TemplateBody(key));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-already-exists");
    }

    [RequiresDockerFact]
    public async Task An_unsupported_class_fails_structural_validation_with_400()
    {
        var client = fixture.CreateAuthorClient("author-1");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/templates",
            TemplateApi.TemplateBody(TemplateApi.NewKey(), @class: "marketing"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task A_malformed_key_is_rejected_with_400()
    {
        var client = fixture.CreateAuthorClient("author-1");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/templates",
            TemplateApi.TemplateBody("Invalid Key!"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    [RequiresDockerFact]
    public async Task Without_a_bearer_token_the_endpoint_returns_401()
    {
        var client = fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/templates",
            TemplateApi.TemplateBody(TemplateApi.NewKey()));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [RequiresDockerFact]
    public async Task Without_the_author_role_the_endpoint_returns_403()
    {
        var client = fixture.CreateClientWithToken("author-1", "Some.Other.Role");

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/v1/templates",
            TemplateApi.TemplateBody(TemplateApi.NewKey()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
