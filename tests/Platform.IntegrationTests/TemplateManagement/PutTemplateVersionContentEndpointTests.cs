using System.Globalization;
using System.Net;
using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PutTemplateVersionContentEndpointTests(TemplateManagementApiFixture fixture)
{
    private static readonly object EmailContent = new
    {
        subject = "Seu pedido",
        body = "<p>Pedido {{orderId}} atualizado.</p>",
        bodyText = "Pedido {{orderId}} atualizado.",
    };

    [RequiresDockerFact]
    public async Task Editing_draft_content_with_the_current_entity_tag_returns_200_and_rotates_it()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/email/pt-BR";

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, EmailContent, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.ToString().ShouldNotBe(etag);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("contentHash").GetString()!.ShouldMatch("^[0-9a-f]{64}$");
        body.GetProperty("editors")[0].GetString().ShouldBe("editor-1");
        JsonElement content = body.GetProperty("contents")[0];
        content.GetProperty("channel").GetString().ShouldBe("email");
        content.GetProperty("locale").GetString().ShouldBe("pt-BR");
        content.GetProperty("bodyHash").GetString()!.ShouldMatch("^[0-9a-f]{64}$");
    }

    [RequiresDockerFact]
    public async Task Editing_content_changes_the_version_content_hash()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/sms/pt";

        HttpResponseMessage first = await client.SendAsync(
            TemplateApi.PutJson(url, new { body = "Código {{code}}" }, etag));
        JsonElement firstBody = await TemplateApi.ReadJsonAsync(first);
        HttpResponseMessage second = await client.SendAsync(TemplateApi.PutJson(
            url,
            new { body = "Código {{code}} expira em {{minutes}} minutos" },
            first.Headers.ETag!.ToString()));
        JsonElement secondBody = await TemplateApi.ReadJsonAsync(second);

        second.StatusCode.ShouldBe(HttpStatusCode.OK);
        secondBody.GetProperty("contentHash").GetString()
            .ShouldNotBe(firstBody.GetProperty("contentHash").GetString());
    }

    [RequiresDockerFact]
    public async Task A_wildcard_if_match_returns_412()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, _) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/email/pt-BR";

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, EmailContent, "*"));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [RequiresDockerFact]
    public async Task A_stale_entity_tag_returns_412()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/email/pt-BR";
        await client.SendAsync(TemplateApi.PutJson(url, EmailContent, etag));

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, EmailContent, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("precondition-failed");
    }

    [RequiresDockerFact]
    public async Task A_missing_if_match_header_returns_412()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, _) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/email/pt-BR";

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, EmailContent, ifMatch: null));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [RequiresDockerFact]
    public async Task Editing_a_published_version_returns_409_with_state_and_allowed_transitions()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        var etag = await SeedPublishedVersionAsync(key);
        var url = $"/v1/templates/{key}/versions/1/content/email/pt-BR";

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, EmailContent, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-state-transition");
        problem.GetProperty("currentStatus").GetString().ShouldBe("published");
        problem.GetProperty("allowedTransitions")[0].GetString().ShouldBe("superseded");
    }

    [RequiresDockerFact]
    public async Task An_unknown_channel_is_rejected_with_400()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/fax/pt-BR";

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, EmailContent, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task Two_editors_are_both_recorded_on_the_version()
    {
        HttpClient authorClient = fixture.CreateAuthorClient("editor-a");
        HttpClient secondClient = fixture.CreateAuthorClient("editor-b");
        var key = await TemplateApi.CreateTemplateAsync(authorClient, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(authorClient, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/email/pt-BR";

        HttpResponseMessage first = await authorClient.SendAsync(TemplateApi.PutJson(url, EmailContent, etag));
        HttpResponseMessage second = await secondClient.SendAsync(
            TemplateApi.PutJson(url, EmailContent, first.Headers.ETag!.ToString()));

        JsonElement body = await TemplateApi.ReadJsonAsync(second);
        string?[] editors = [.. body.GetProperty("editors").EnumerateArray().Select(editor => editor.GetString())];
        editors.ShouldBe(["editor-a", "editor-b"]);
    }

    [RequiresDockerFact]
    public async Task A_body_longer_than_the_source_ceiling_returns_400_naming_the_ceiling()
    {
        HttpClient client = fixture.CreateAuthorClient("editor-1");
        var key = await TemplateApi.CreateTemplateAsync(client, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(client, key);
        var url = $"/v1/templates/{key}/versions/{version}/content/email/pt-BR";
        var oversized = new { subject = "Seu pedido", body = new string('a', 200_000) };

        HttpResponseMessage response = await client.SendAsync(TemplateApi.PutJson(url, oversized, etag));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("errors").GetProperty("Body")[0].GetString()!
            .ShouldContain(TemplateSourceSize.MaxChars.ToString(CultureInfo.InvariantCulture));
    }

    private async Task<string> SeedPublishedVersionAsync(string key)
    {
        var entityTag = string.Empty;
        await fixture.ExecuteDbAsync(async dbContext =>
        {
            var version = TemplateVersion.Rehydrate(new TemplateVersionState
            {
                TemplateKey = key,
                Version = 1,
                Status = "published",
                CreatedBy = "seed-author",
                CreatedAt = DateTimeOffset.UtcNow,
                Contents = [new TemplateContentState("email", "pt-BR", "Assunto", "<p>corpo</p>", "corpo")],
            });
            dbContext.TemplateVersions.Add(version);
            await dbContext.SaveChangesAsync();
            entityTag = version.EntityTag;
        });
        return entityTag;
    }
}
