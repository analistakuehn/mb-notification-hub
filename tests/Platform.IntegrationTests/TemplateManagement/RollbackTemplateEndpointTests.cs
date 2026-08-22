using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class RollbackTemplateEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_rollback_republishes_the_source_content_as_a_new_version_with_provenance()
    {
        HttpClient author = fixture.CreateAuthorClient("author-rb-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-rb-1");
        HttpClient rollbackCaller = fixture.CreatePublisherClient("publisher-rb-2");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, first);
        var firstHash = await ContentHashOfAsync(author, key, first);
        var second = await CreateEditedDraftAsync(author, key, first);
        await TemplateApi.PublishAsync(publisher, key, second);

        HttpResponseMessage response = await rollbackCaller.PostAsJsonAsync(
            $"/v1/templates/{key}/rollback", new { toVersion = first });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        var third = body.GetProperty("version").GetInt32();
        third.ShouldBe(second + 1);
        body.GetProperty("status").GetString().ShouldBe("published");
        body.GetProperty("rolledBackFrom").GetInt32().ShouldBe(first);
        body.GetProperty("contentHash").GetString().ShouldBe(firstHash);
        body.GetProperty("supersededVersion").GetInt32().ShouldBe(second);

        HttpResponseMessage secondVersion = await author.GetAsync($"/v1/templates/{key}/versions/{second}");
        (await TemplateApi.ReadJsonAsync(secondVersion)).GetProperty("status").GetString().ShouldBe("superseded");
        await fixture.ExecuteDbAsync(async db =>
        {
            Approval approval = await db.Approvals.AsNoTracking().SingleAsync(candidate =>
                candidate.SubjectId == key && candidate.SubjectVersion == third);
            approval.ApproverOid.ShouldBe("publisher-rb-2");
            approval.ContentHash.ShouldBe(firstHash);

            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "template.rollback" && candidate.EntityId == $"{key}:{third}");
            audit.ActorId.ShouldBe("publisher-rb-2");
            audit.DetailsJson.ShouldContain($"\"rolledBackFrom\": {first}");
        });
    }

    [RequiresDockerFact]
    public async Task The_author_of_the_source_version_cannot_roll_back_to_it()
    {
        HttpClient authorPublisher = fixture.CreateClientWithToken(
            "author-rb-3",
            AuthorizationSetup.AuthorRole,
            AuthorizationSetup.PublisherRole);
        HttpClient publisher = fixture.CreatePublisherClient("publisher-rb-3");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(authorPublisher);
        await TemplateApi.PublishAsync(publisher, key, first);
        var second = await CreateEditedDraftAsync(authorPublisher, key, first);
        await TemplateApi.PublishAsync(publisher, key, second);

        HttpResponseMessage response = await authorPublisher.PostAsJsonAsync(
            $"/v1/templates/{key}/rollback", new { toVersion = first });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("four-eyes-violation");
        await fixture.ExecuteDbAsync(async db =>
            (await db.AuditEvents.AsNoTracking().AnyAsync(candidate =>
                    candidate.Action == "template.rollback"
                    && candidate.EntityId.StartsWith($"{key}:")))
                .ShouldBeFalse());
    }

    [RequiresDockerFact]
    public async Task A_version_never_published_cannot_be_a_rollback_target()
    {
        HttpClient author = fixture.CreateAuthorClient("author-rb-4");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-rb-4");
        (var key, var draft) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/rollback", new { toVersion = draft });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-state-transition");
        problem.GetProperty("currentStatus").GetString().ShouldBe("draft");
    }

    [RequiresDockerFact]
    public async Task Rolling_back_to_an_unknown_version_returns_404()
    {
        HttpClient author = fixture.CreateAuthorClient("author-rb-5");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-rb-5");
        (var key, _) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/rollback", new { toVersion = 99 });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-version-not-found");
    }

    /// <summary>Opens a clone of the given version and edits its body so the content differs.</summary>
    private static async Task<int> CreateEditedDraftAsync(HttpClient authorClient, string key, int fromVersion)
    {
        HttpResponseMessage draftResponse = await authorClient.PostAsJsonAsync(
            $"/v1/templates/{key}/versions", new { fromVersion });
        draftResponse.EnsureSuccessStatusCode();
        JsonElement draft = await TemplateApi.ReadJsonAsync(draftResponse);
        var version = draft.GetProperty("version").GetInt32();
        var etag = draftResponse.Headers.ETag!.ToString();
        await TemplateApi.PutContentAsync(authorClient, key, version, "email/pt-BR", new
        {
            subject = "Pedido {{ orderId }}",
            body = "<p>Pedido {{ orderId }} mudou de etapa.</p>",
            bodyText = "Pedido {{ orderId }} mudou de etapa.",
        }, etag);
        return version;
    }

    private static async Task<string> ContentHashOfAsync(HttpClient client, string key, int version)
    {
        HttpResponseMessage response = await client.GetAsync($"/v1/templates/{key}/versions/{version}");
        response.EnsureSuccessStatusCode();
        return (await TemplateApi.ReadJsonAsync(response)).GetProperty("contentHash").GetString()!;
    }
}
