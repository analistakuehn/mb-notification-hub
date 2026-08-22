using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class CreateTemplateVersionEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Opening_the_first_draft_returns_201_with_an_entity_tag_and_the_author()
    {
        HttpClient client = fixture.CreateAuthorClient("author-42");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.PostAsync($"/v1/templates/{key}/versions", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location!.ToString().ShouldBe($"/v1/templates/{key}/versions/1");
        response.Headers.ETag.ShouldNotBeNull();
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("templateKey").GetString().ShouldBe(key);
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("status").GetString().ShouldBe("draft");
        body.GetProperty("createdBy").GetString().ShouldBe("author-42");
        body.GetProperty("editors").GetArrayLength().ShouldBe(0);
        body.GetProperty("contents").GetArrayLength().ShouldBe(0);
        body.TryGetProperty("entityTag", out _).ShouldBeFalse();
    }

    [RequiresDockerFact]
    public async Task A_second_draft_for_the_same_template_returns_409_draft_already_exists()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        await TemplateApi.CreateDraftAsync(client, key);

        HttpResponseMessage response = await client.PostAsync($"/v1/templates/{key}/versions", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("draft-already-exists");
    }

    [RequiresDockerFact]
    public async Task Cloning_from_a_published_version_copies_contents_schema_and_content_hash()
    {
        HttpClient client = fixture.CreateAuthorClient("author-2");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        var sourceHash = await SeedPublishedVersionAsync(key);

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions",
            new { fromVersion = 1 });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("version").GetInt32().ShouldBe(2);
        body.GetProperty("status").GetString().ShouldBe("draft");
        body.GetProperty("contentHash").GetString().ShouldBe(sourceHash);
        body.GetProperty("contents").GetArrayLength().ShouldBe(1);
        JsonElement content = body.GetProperty("contents")[0];
        content.GetProperty("channel").GetString().ShouldBe("email");
        content.GetProperty("locale").GetString().ShouldBe("pt-BR");
        content.GetProperty("subject").GetString().ShouldBe("Assunto");
        body.GetProperty("variablesSchema").GetProperty("type").GetString().ShouldBe("object");
    }

    [RequiresDockerFact]
    public async Task Cloning_from_a_missing_version_returns_404()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());

        HttpResponseMessage response = await client.PostAsJsonAsync(
            $"/v1/templates/{key}/versions",
            new { fromVersion = 7 });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-version-not-found");
    }

    [RequiresDockerFact]
    public async Task Opening_a_draft_for_an_unknown_template_returns_404()
    {
        HttpClient client = fixture.CreateAuthorClient("author-1");

        HttpResponseMessage response = await client.PostAsync(
            $"/v1/templates/{TemplateApi.NewKey()}/versions",
            content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-not-found");
    }

    private async Task<string> SeedPublishedVersionAsync(string key)
    {
        var contentHash = string.Empty;
        await fixture.ExecuteDbAsync(async dbContext =>
        {
            var version = TemplateVersion.Rehydrate(new TemplateVersionState
            {
                TemplateKey = key,
                Version = 1,
                Status = "published",
                VariablesSchemaJson = """{"type":"object"}""",
                CreatedBy = "seed-author",
                CreatedAt = DateTimeOffset.UtcNow,
                Editors = ["seed-author"],
                Contents = [new TemplateContentState("email", "pt-BR", "Assunto", "<p>corpo</p>", "corpo")],
            });
            dbContext.TemplateVersions.Add(version);
            await dbContext.SaveChangesAsync();
            contentHash = version.ContentHash;
        });
        return contentHash;
    }
}
