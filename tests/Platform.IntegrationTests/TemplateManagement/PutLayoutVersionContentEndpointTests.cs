using System.Globalization;
using System.Net;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PutLayoutVersionContentEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_body_longer_than_the_source_ceiling_returns_400_naming_the_ceiling()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-lay-1");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await LayoutApi.CreateDraftAsync(client, key);
        var url = $"/v1/layouts/{key}/versions/{version}/content/email/pt-BR";
        var oversized = new { body = new string('a', 200_000) };

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, oversized, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("errors").GetProperty("Body")[0].GetString()!
            .ShouldContain(TemplateSourceSize.MaxChars.ToString(CultureInfo.InvariantCulture));
    }

    [RequiresDockerFact]
    public async Task A_body_the_source_ceiling_admits_is_stored()
    {
        // The refusal above is only worth something beside a body of the same
        // family that goes through, otherwise a route that refused everything
        // would satisfy it.
        HttpClient client = fixture.CreateAuthorClient("editor-lay-2");
        var key = await LayoutApi.CreateLayoutAsync(client, LayoutApi.NewKey(), defaultLocale: "pt-BR");
        (var version, var etag) = await LayoutApi.CreateDraftAsync(client, key);
        var url = $"/v1/layouts/{key}/versions/{version}/content/email/pt-BR";
        var admitted = new { body = new string('a', 100_000) + "{{ content }}" };

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, admitted, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("contentHash").GetString()!.ShouldMatch("^[0-9a-f]{64}$");
    }
}
