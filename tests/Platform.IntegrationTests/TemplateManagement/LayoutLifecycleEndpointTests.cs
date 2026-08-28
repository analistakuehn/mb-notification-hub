using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class LayoutLifecycleEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Creating_a_layout_returns_201_and_writes_its_creation_to_the_audit_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-1");
        var key = LayoutApi.NewKey();

        HttpResponseMessage response = await author.PostAsJsonAsync(
            "/v1/layouts",
            new { key, ownerTeam = "design-system", defaultLocale = "pt-BR" });

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("active");
        body.GetProperty("ownerTeam").GetString().ShouldBe("design-system");
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "layout.created" && candidate.EntityId == key);
            audit.ActorId.ShouldBe("author-lay-1");
            audit.EntityType.ShouldBe("layout");
        });
    }

    [RequiresDockerFact]
    public async Task Editing_a_layout_draft_requires_the_current_entity_tag()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-2");
        var key = await LayoutApi.CreateLayoutAsync(author, LayoutApi.NewKey());
        (var version, _) = await LayoutApi.CreateDraftAsync(author, key);

        HttpResponseMessage response = await author.SendAsync(TemplateApi.PutJson(
            $"/v1/layouts/{key}/versions/{version}/content/email/pt-BR",
            new { body = "<html>{{ content }}</html>" },
            ifMatch: "\"etag-antigo\""));

        response.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
    }

    [RequiresDockerFact]
    public async Task Validating_a_wrapper_without_the_content_placeholder_reports_the_failed_check()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-3");
        var key = await LayoutApi.CreateLayoutAsync(author, LayoutApi.NewKey());
        (var version, var etag) = await LayoutApi.CreateDraftAsync(author, key);
        await LayoutApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            body = "<html>sem espaço para o corpo</html>",
        }, etag);

        HttpResponseMessage response = await author.PostAsync(
            $"/v1/layouts/{key}/versions/{version}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("passed").GetBoolean().ShouldBeFalse();
        List<JsonElement> checks = [.. body.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "content-placeholder"
            && check.GetProperty("status").GetString() == "failed");
    }

    [RequiresDockerFact]
    public async Task A_wrapper_without_the_placeholder_does_not_publish_returning_422_with_the_report()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-4");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lay-4");
        var key = await LayoutApi.CreateLayoutAsync(author, LayoutApi.NewKey());
        (var version, var etag) = await LayoutApi.CreateDraftAsync(author, key);
        await LayoutApi.PutContentAsync(author, key, version, "email/pt-BR", new
        {
            body = "<html>estático</html>",
        }, etag);

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/layouts/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("layout-validation-failed");
        List<JsonElement> checks = [.. problem.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "content-placeholder"
            && check.GetProperty("status").GetString() == "failed");
    }

    [RequiresDockerFact]
    public async Task The_author_cannot_publish_their_own_layout_draft_even_with_the_publisher_role()
    {
        HttpClient authorPublisher = fixture.CreateClientWithToken(
            "author-lay-5",
            AuthorizationSetup.AuthorRole,
            AuthorizationSetup.PublisherRole);
        (var key, var version) = await LayoutApi.CreatePublishableDraftAsync(authorPublisher);

        HttpResponseMessage response = await authorPublisher.PostAsync(
            $"/v1/layouts/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("four-eyes-violation");
        await fixture.ExecuteAuditDbAsync(async db =>
            (await db.Approvals.AsNoTracking().AnyAsync(candidate => candidate.SubjectId == key))
                .ShouldBeFalse());
    }

    [RequiresDockerFact]
    public async Task A_distinct_publisher_publishes_recording_approval_and_audit_together()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-6");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lay-6");
        (var key, var version) = await LayoutApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/layouts/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("published");
        var contentHash = body.GetProperty("contentHash").GetString()!;
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            Approval approval = await db.Approvals.AsNoTracking().SingleAsync(candidate =>
                candidate.SubjectType == "layout_version"
                && candidate.SubjectId == key
                && candidate.SubjectVersion == version);
            approval.ContentHash.ShouldBe(contentHash);
            approval.ApproverOid.ShouldBe("publisher-lay-6");

            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "layout.version.published"
                && candidate.EntityId == $"{key}:{version}");
            audit.ActorId.ShouldBe("publisher-lay-6");
            audit.EntityType.ShouldBe("layout_version");
        });
    }

    [RequiresDockerFact]
    public async Task Publishing_the_next_version_supersedes_the_previous_one()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-7");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lay-7");
        (var key, var first) = await LayoutApi.CreatePublishableDraftAsync(author);
        await LayoutApi.PublishAsync(publisher, key, first);
        HttpResponseMessage draftResponse = await author.PostAsJsonAsync(
            $"/v1/layouts/{key}/versions", new { fromVersion = first });
        draftResponse.EnsureSuccessStatusCode();
        var second = (await TemplateApi.ReadJsonAsync(draftResponse)).GetProperty("version").GetInt32();

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/layouts/{key}/versions/{second}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("supersededVersion").GetInt32().ShouldBe(first);
        HttpResponseMessage firstVersion = await author.GetAsync($"/v1/layouts/{key}/versions/{first}");
        (await TemplateApi.ReadJsonAsync(firstVersion)).GetProperty("status").GetString().ShouldBe("superseded");
    }

    [RequiresDockerFact]
    public async Task Rollback_republishes_a_previous_version_and_audits_it()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-8");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lay-8");
        (var key, var first) = await LayoutApi.CreatePublishableDraftAsync(author);
        await LayoutApi.PublishAsync(publisher, key, first);
        HttpResponseMessage draftResponse = await author.PostAsJsonAsync(
            $"/v1/layouts/{key}/versions", new { fromVersion = first });
        draftResponse.EnsureSuccessStatusCode();
        JsonElement draft = await TemplateApi.ReadJsonAsync(draftResponse);
        var second = draft.GetProperty("version").GetInt32();
        await LayoutApi.PutContentAsync(author, key, second, "email/pt-BR", new
        {
            body = "<html>v2 {{ content }}</html>",
        }, (await author.GetAsync($"/v1/layouts/{key}/versions/{second}")).Headers.ETag!.ToString());
        await LayoutApi.PublishAsync(publisher, key, second);

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/layouts/{key}/rollback", new { toVersion = first });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("rolledBackFrom").GetInt32().ShouldBe(first);
        body.GetProperty("supersededVersion").GetInt32().ShouldBe(second);
        var third = body.GetProperty("version").GetInt32();
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "layout.rollback" && candidate.EntityId == $"{key}:{third}");
            audit.ActorId.ShouldBe("publisher-lay-8");
        });
    }

    [RequiresDockerFact]
    public async Task Deprecating_and_disabling_a_layout_are_audited_as_their_own_events()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-9");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lay-9");
        var key = await LayoutApi.CreateLayoutAsync(author, LayoutApi.NewKey());

        HttpResponseMessage deprecated = await publisher.PostAsJsonAsync(
            $"/v1/layouts/{key}/deprecate",
            new { reason = "visual-identity-change", note = "identidade visual antiga" });
        HttpResponseMessage disabled = await publisher.PostAsJsonAsync(
            $"/v1/layouts/{key}/disable",
            new { reason = "superseded-by-new-version", note = "substituído em produção" });

        deprecated.StatusCode.ShouldBe(HttpStatusCode.OK);
        disabled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TemplateApi.ReadJsonAsync(disabled)).GetProperty("status").GetString().ShouldBe("disabled");
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent deprecatedAudit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "layout.deprecated" && candidate.EntityId == key);
            deprecatedAudit.DetailsJson.ShouldContain("identidade visual antiga");
            AuditEvent disabledAudit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "layout.disabled" && candidate.EntityId == key);
            disabledAudit.DetailsJson.ShouldContain("substituído em produção");
        });
    }

    [RequiresDockerFact]
    public async Task A_deprecated_layout_does_not_publish()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-10");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lay-10");
        (var key, var version) = await LayoutApi.CreatePublishableDraftAsync(author);
        (await publisher.PostAsJsonAsync($"/v1/layouts/{key}/deprecate",
            new { reason = "retired", note = "aposentando" }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await publisher.PostAsync(
            $"/v1/layouts/{key}/versions/{version}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-state-transition");
        problem.GetProperty("currentStatus").GetString().ShouldBe("deprecated");
    }

    [RequiresDockerFact]
    public async Task The_catalog_lists_the_layout_and_the_detail_shows_its_versions()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-11");
        (var key, var version) = await LayoutApi.CreatePublishableDraftAsync(author);

        HttpResponseMessage list = await author.GetAsync($"/v1/layouts?owner=design-system&limit=200");
        HttpResponseMessage detail = await author.GetAsync($"/v1/layouts/{key}");

        list.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement items = (await TemplateApi.ReadJsonAsync(list)).GetProperty("items");
        items.EnumerateArray().ShouldContain(item => item.GetProperty("key").GetString() == key);
        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(detail);
        body.GetProperty("versions").EnumerateArray().ShouldContain(entry =>
            entry.GetProperty("version").GetInt32() == version
            && entry.GetProperty("status").GetString() == "draft");
    }

    [RequiresDockerFact]
    public async Task Only_one_draft_can_be_open_per_layout()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lay-12");
        var key = await LayoutApi.CreateLayoutAsync(author, LayoutApi.NewKey());
        await LayoutApi.CreateDraftAsync(author, key);

        HttpResponseMessage response = await author.PostAsync($"/v1/layouts/{key}/versions", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("draft-already-exists");
    }
}
