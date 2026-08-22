using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class VersionDiffEndpointTests(TemplateManagementApiFixture fixture)
{
    private static readonly string[] RequiredOrderId = ["orderId"];

    [RequiresDockerFact]
    public async Task Template_diff_reports_added_changed_contents_and_schema_fields_between_two_versions()
    {
        var author = fixture.CreateAuthorClient("author-diff-1");
        var publisher = fixture.CreatePublisherClient("publisher-diff-1");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, first);
        HttpResponseMessage draftResponse = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions", new { fromVersion = first });
        draftResponse.EnsureSuccessStatusCode();
        var second = (await TemplateApi.ReadJsonAsync(draftResponse)).GetProperty("version").GetInt32();
        var etag = draftResponse.Headers.ETag!.ToString();
        etag = await TemplateApi.PutContentAsync(author, key, second, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} alterado.</p>",
            bodyText = "Pedido {{ orderId }} atualizado.",
        }, etag);
        etag = await TemplateApi.PutContentAsync(author, key, second, "sms/pt-BR", new
        {
            body = "Pedido {{ orderId }} atualizado.",
        }, etag);
        await TemplateApi.PutSchemaAsync(author, key, second, new
        {
            type = "object",
            properties = new
            {
                orderId = new { type = "string" },
                amount = new { type = "number" },
            },
            required = RequiredOrderId,
        }, etag);

        HttpResponseMessage response = await author.GetAsync(
            $"/v1/templates/{key}/versions/{second}/diff?against={first}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("templateKey").GetString().ShouldBe(key);
        body.GetProperty("version").GetInt32().ShouldBe(second);
        body.GetProperty("againstVersion").GetInt32().ShouldBe(first);

        JsonElement contents = body.GetProperty("contents");
        List<JsonElement> added = [.. contents.GetProperty("added").EnumerateArray()];
        added.Count.ShouldBe(1);
        added[0].GetProperty("channel").GetString().ShouldBe("sms");
        added[0].GetProperty("locale").GetString().ShouldBe("pt-BR");
        contents.GetProperty("removed").GetArrayLength().ShouldBe(0);
        List<JsonElement> changed = [.. contents.GetProperty("changed").EnumerateArray()];
        changed.Count.ShouldBe(1);
        changed[0].GetProperty("channel").GetString().ShouldBe("email");
        List<string> fields = [.. changed[0].GetProperty("fields").EnumerateArray().Select(field => field.GetString()!)];
        fields.ShouldBe(["body"]);

        JsonElement schema = body.GetProperty("variablesSchema");
        List<string> addedFields = [.. schema.GetProperty("addedFields").EnumerateArray().Select(field => field.GetString()!)];
        addedFields.ShouldBe(["amount"]);
        schema.GetProperty("removedFields").GetArrayLength().ShouldBe(0);
        schema.GetProperty("changedFields").GetArrayLength().ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task Template_diff_without_the_against_parameter_returns_400()
    {
        var author = fixture.CreateAuthorClient("author-diff-2");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await author.GetAsync(
            $"/v1/templates/{key}/versions/{version}/diff");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
    }

    [RequiresDockerFact]
    public async Task Template_diff_against_an_unknown_version_returns_404()
    {
        var author = fixture.CreateAuthorClient("author-diff-3");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await author.GetAsync(
            $"/v1/templates/{key}/versions/{version}/diff?against=99");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-version-not-found");
    }

    [RequiresDockerFact]
    public async Task Layout_diff_reports_the_fields_that_changed_per_channel_and_locale()
    {
        var author = fixture.CreateAuthorClient("author-diff-4");
        var publisher = fixture.CreatePublisherClient("publisher-diff-4");
        (var key, var first) = await LayoutApi.CreatePublishableDraftAsync(author);
        await LayoutApi.PublishAsync(publisher, key, first);
        HttpResponseMessage draftResponse = await author.PostAsJsonAsync(
            $"/v1/layouts/{key}/versions", new { fromVersion = first });
        draftResponse.EnsureSuccessStatusCode();
        var second = (await TemplateApi.ReadJsonAsync(draftResponse)).GetProperty("version").GetInt32();
        await LayoutApi.PutContentAsync(author, key, second, "email/pt-BR", new
        {
            body = "<html><header>MB</header>{{ content }}<footer>rodapé</footer></html>",
            bodyText = "MB\n{{ content }}",
        }, draftResponse.Headers.ETag!.ToString());

        HttpResponseMessage response = await author.GetAsync(
            $"/v1/layouts/{key}/versions/{second}/diff?against={first}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("layoutKey").GetString().ShouldBe(key);
        JsonElement contents = body.GetProperty("contents");
        contents.GetProperty("added").GetArrayLength().ShouldBe(0);
        contents.GetProperty("removed").GetArrayLength().ShouldBe(0);
        List<JsonElement> changed = [.. contents.GetProperty("changed").EnumerateArray()];
        changed.Count.ShouldBe(1);
        changed[0].GetProperty("channel").GetString().ShouldBe("email");
        changed[0].GetProperty("locale").GetString().ShouldBe("pt-BR");
        List<string> fields = [.. changed[0].GetProperty("fields").EnumerateArray().Select(field => field.GetString()!)];
        fields.ShouldBe(["bodyText"]);
    }
}
