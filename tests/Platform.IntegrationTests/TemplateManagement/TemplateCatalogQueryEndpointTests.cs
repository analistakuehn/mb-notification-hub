using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class TemplateCatalogQueryEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_template_detail_includes_metadata_and_version_history()
    {
        var client = fixture.CreateAuthorClient("author-1");
        string key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await TemplateApi.CreateDraftAsync(client, key);

        HttpResponseMessage response = await client.GetAsync($"/v1/templates/{key}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("key").GetString().ShouldBe(key);
        body.GetProperty("status").GetString().ShouldBe("active");
        body.GetProperty("versions").GetArrayLength().ShouldBe(1);
        JsonElement version = body.GetProperty("versions")[0];
        version.GetProperty("version").GetInt32().ShouldBe(1);
        version.GetProperty("status").GetString().ShouldBe("draft");
        version.GetProperty("createdBy").GetString().ShouldBe("author-1");
    }

    [RequiresDockerFact]
    public async Task An_unknown_template_returns_404()
    {
        var client = fixture.CreateAuthorClient("author-1");

        HttpResponseMessage response = await client.GetAsync($"/v1/templates/{TemplateApi.NewKey()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-not-found");
    }

    [RequiresDockerFact]
    public async Task The_version_detail_returns_contents_and_sets_the_entity_tag_header()
    {
        var client = fixture.CreateAuthorClient("author-1");
        string key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (int version, string etag) = await TemplateApi.CreateDraftAsync(client, key);
        await client.SendAsync(TemplateApi.PutJson(
            $"/v1/templates/{key}/versions/{version}/content/sms/pt",
            new { body = "oi {{name}}" },
            etag));

        HttpResponseMessage response = await client.GetAsync($"/v1/templates/{key}/versions/{version}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag.ShouldNotBeNull();
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("templateKey").GetString().ShouldBe(key);
        body.GetProperty("contents")[0].GetProperty("channel").GetString().ShouldBe("sms");
        body.GetProperty("contents")[0].GetProperty("locale").GetString().ShouldBe("pt");
    }

    [RequiresDockerFact]
    public async Task An_unknown_version_returns_404()
    {
        var client = fixture.CreateAuthorClient("author-1");
        string key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.GetAsync($"/v1/templates/{key}/versions/9");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [RequiresDockerFact]
    public async Task The_catalog_filters_by_application_owner_and_status()
    {
        var client = fixture.CreateAuthorClient("author-1");
        string application = $"app-{Guid.NewGuid():N}"[..20];
        string key = TemplateApi.NewKey();
        await client.PostAsJsonAsync("/v1/templates", TemplateApi.TemplateBody(key, application));
        await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.GetAsync(
            $"/v1/templates?application={application}&status=active&owner=growth-squad");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("items").GetArrayLength().ShouldBe(1);
        body.GetProperty("items")[0].GetProperty("key").GetString().ShouldBe(key);
        body.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [RequiresDockerFact]
    public async Task The_catalog_pages_with_an_opaque_cursor_without_repeating_items()
    {
        var client = fixture.CreateAuthorClient("author-1");
        string application = $"app-{Guid.NewGuid():N}"[..20];
        for (int i = 0; i < 3; i++)
        {
            await client.PostAsJsonAsync(
                "/v1/templates",
                TemplateApi.TemplateBody(TemplateApi.NewKey(), application));
        }

        HttpResponseMessage firstPage = await client.GetAsync(
            $"/v1/templates?application={application}&limit=2");
        JsonElement firstBody = await TemplateApi.ReadJsonAsync(firstPage);
        string cursor = firstBody.GetProperty("nextCursor").GetString()!;
        HttpResponseMessage secondPage = await client.GetAsync(
            $"/v1/templates?application={application}&limit=2&cursor={cursor}");
        JsonElement secondBody = await TemplateApi.ReadJsonAsync(secondPage);

        firstBody.GetProperty("items").GetArrayLength().ShouldBe(2);
        secondBody.GetProperty("items").GetArrayLength().ShouldBe(1);
        secondBody.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);
        string?[] firstKeys = [.. firstBody.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("key").GetString())];
        string? lastKey = secondBody.GetProperty("items")[0].GetProperty("key").GetString();
        firstKeys.ShouldNotContain(lastKey);
    }

    [RequiresDockerFact]
    public async Task An_unsupported_status_filter_is_rejected_with_400()
    {
        var client = fixture.CreateAuthorClient("author-1");

        HttpResponseMessage response = await client.GetAsync("/v1/templates?status=archived");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task A_malformed_cursor_is_rejected_with_400()
    {
        var client = fixture.CreateAuthorClient("author-1");

        HttpResponseMessage response = await client.GetAsync("/v1/templates?cursor=%2Fnot-valid%2F");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
