using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class PublishTemplateVersionEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_distinct_publisher_publishes_a_valid_draft_recording_approval_and_audit_together()
    {
        HttpClient author = fixture.CreateAuthorClient("author-pub-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-pub-1");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("published");
        body.GetProperty("supersededVersion").ValueKind.ShouldBe(JsonValueKind.Null);
        var contentHash = body.GetProperty("contentHash").GetString()!;

        await fixture.ExecuteDbAsync(async db =>
        {
            Approval approval = await db.Approvals.AsNoTracking().SingleAsync(candidate =>
                candidate.SubjectType == "template_version"
                && candidate.SubjectId == key
                && candidate.SubjectVersion == version);
            approval.ContentHash.ShouldBe(contentHash);
            approval.ApproverOid.ShouldBe("publisher-pub-1");
            approval.Role.ShouldBe("publisher");

            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "template.version.published"
                && candidate.EntityId == $"{key}:{version}");
            audit.ActorId.ShouldBe("publisher-pub-1");
            audit.ActorType.ShouldBe("user");
            audit.Seq.ShouldBeGreaterThan(0);
            audit.DetailsJson.ShouldContain(contentHash);
            audit.DetailsJson.ShouldContain("\"passed\": true");
        });
    }

    [RequiresDockerFact]
    public async Task Publishing_the_next_version_supersedes_the_previous_one()
    {
        HttpClient author = fixture.CreateAuthorClient("author-pub-2");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-pub-2");
        (var key, var first) = await TemplateApi.CreatePublishableDraftAsync(author);
        await TemplateApi.PublishAsync(publisher, key, first);
        HttpResponseMessage draftResponse = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/versions", new { fromVersion = first });
        draftResponse.EnsureSuccessStatusCode();
        JsonElement draft = await TemplateApi.ReadJsonAsync(draftResponse);
        var second = draft.GetProperty("version").GetInt32();

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{second}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("supersededVersion").GetInt32().ShouldBe(first);
        HttpResponseMessage firstVersion = await author.GetAsync($"/v1/templates/{key}/versions/{first}");
        JsonElement firstBody = await TemplateApi.ReadJsonAsync(firstVersion);
        firstBody.GetProperty("status").GetString().ShouldBe("superseded");
    }

    [RequiresDockerFact]
    public async Task A_draft_failing_validation_returns_422_with_the_full_report_in_checks()
    {
        HttpClient author = fixture.CreateAuthorClient("author-pub-3");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-pub-3");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());
        (var version, var etag) = await TemplateApi.CreateDraftAsync(author, key);
        await TemplateApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            subject = "Oi",
            body = "<p>Olá {{ nome }}</p>",
        }, etag);

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-validation-failed");
        List<JsonElement> checks = [.. problem.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "variables-declared"
            && check.GetProperty("status").GetString() == "failed");

        HttpResponseMessage versionResponse = await author.GetAsync($"/v1/templates/{key}/versions/{version}");
        JsonElement versionBody = await TemplateApi.ReadJsonAsync(versionResponse);
        versionBody.GetProperty("status").GetString().ShouldBe("draft");
        await fixture.ExecuteDbAsync(async db =>
        {
            (await db.Approvals.AsNoTracking().AnyAsync(candidate => candidate.SubjectId == key))
                .ShouldBeFalse();
            (await db.AuditEvents.AsNoTracking().AnyAsync(candidate =>
                    candidate.Action == "template.version.published"
                    && candidate.EntityId == $"{key}:{version}"))
                .ShouldBeFalse();
        });
    }

    [RequiresDockerFact]
    public async Task The_author_cannot_publish_their_own_draft_even_with_the_publisher_role()
    {
        HttpClient authorPublisher = fixture.CreateClientWithToken(
            "author-pub-4",
            AuthorizationSetup.AuthorRole,
            AuthorizationSetup.PublisherRole);
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(authorPublisher);

        HttpResponseMessage response = await authorPublisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("four-eyes-violation");
        await fixture.ExecuteDbAsync(async db =>
            (await db.Approvals.AsNoTracking().AnyAsync(candidate => candidate.SubjectId == key))
                .ShouldBeFalse());
    }

    [RequiresDockerFact]
    public async Task A_deprecated_template_does_not_publish()
    {
        HttpClient author = fixture.CreateAuthorClient("author-pub-5");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-pub-5");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);
        HttpResponseMessage deprecated = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate", new { reason = "substituído pelo fluxo novo" });
        deprecated.EnsureSuccessStatusCode();

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-state-transition");
        problem.GetProperty("currentStatus").GetString().ShouldBe("deprecated");
    }

    [RequiresDockerFact]
    public async Task An_author_only_token_cannot_reach_the_publish_endpoint()
    {
        HttpClient author = fixture.CreateAuthorClient("author-pub-6");
        (var key, var version) = await TemplateApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await author.PostAsync(
            $"/v1/templates/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
