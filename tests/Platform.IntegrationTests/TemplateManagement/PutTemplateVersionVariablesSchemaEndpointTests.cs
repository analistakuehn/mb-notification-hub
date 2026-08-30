using System.Net;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PutTemplateVersionVariablesSchemaEndpointTests(TemplateManagementApiFixture fixture)
{
    private static readonly string[] NotAnObject = ["not", "an", "object"];

    private static readonly object Schema = new
    {
        type = "object",
        properties = new { orderId = new { type = "string" } },
        required = new[] { "orderId" },
    };

    [RequiresDockerFact]
    public async Task Replacing_the_schema_with_the_current_entity_tag_returns_200_and_stores_it()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/variables-schema";

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, Schema, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.ToString().ShouldNotBe(etag);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("variablesSchema").GetProperty("type").GetString().ShouldBe("object");
        body.GetProperty("variablesSchema").GetProperty("required")[0].GetString().ShouldBe("orderId");
        body.GetProperty("editors")[0].GetString().ShouldBe("editor-1");
    }

    [RequiresDockerFact]
    public async Task Replacing_the_schema_changes_the_version_content_hash()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/variables-schema";
        HttpResponseMessage before = await client.GetAsync($"/v1/templates/{key}/versions/{version}");
        JsonElement beforeBody = await TemplateApi.ReadJsonAsync(before);

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, Schema, etag));

        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("contentHash").GetString()
            .ShouldNotBe(beforeBody.GetProperty("contentHash").GetString());
    }

    [RequiresDockerFact]
    public async Task A_schema_that_is_not_a_json_object_is_rejected_with_400()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/variables-schema";

        HttpResponseMessage response = await client.SendAsync(
            TemplateApi.PutJson(url, NotAnObject, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    [RequiresDockerFact]
    public async Task A_stale_entity_tag_returns_412()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/variables-schema";
        await client.SendAsync(TemplateApi.PutJson(url, Schema, etag));

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, Schema, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    /// <summary>
    /// The author's door for the same rule the producer payload already has. A
    /// schema whose escape names no character parses, binds and reaches the
    /// aggregate; every step past this point transcodes it, so without a
    /// refusal here the endpoint answers by failing instead of by refusing.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_schema_whose_escape_names_no_character_is_refused_with_400()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/variables-schema";

        // The premise, asserted rather than assumed: the escape is still six
        // characters on the wire, and not a code unit the compiler folded.
        UnreadableDocumentSeed.SchemaWithSurrogateInValue
            .Contains(UnreadableDocumentSeed.LoneSurrogateEscape, StringComparison.Ordinal)
            .ShouldBeTrue("O corpo enviado deve carregar o escape cru.");

        HttpResponseMessage response = await client.SendAsync(UnreadableDocumentSeed.PutRawJson(
            url, UnreadableDocumentSeed.SchemaWithSurrogateInValue, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("variables-schema-unreadable");

        // Nothing was stored: the draft still answers with no schema at all.
        HttpResponseMessage stored = await client.GetAsync($"/v1/templates/{key}/versions/{version}");
        JsonElement body = await TemplateApi.ReadJsonAsync(stored);
        body.TryGetProperty("variablesSchema", out JsonElement schema).ShouldBeTrue();
        schema.ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
