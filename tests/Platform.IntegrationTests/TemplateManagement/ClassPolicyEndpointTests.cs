using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Authorization;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class ClassPolicyEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Putting_a_definition_without_a_draft_opens_version_1_and_returns_its_entity_tag()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-1");
        var application = ClassPolicyApi.NewApplication();

        HttpResponseMessage response = await author.SendAsync(TemplateApi.PutJson(
            $"{ClassPolicyApi.PolicyUrl(application)}/draft", ClassPolicyApi.Definition(), ifMatch: null));

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.ETag.ShouldNotBeNull();
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("status").GetString().ShouldBe("draft");
        body.GetProperty("schemaVersion").GetInt32().ShouldBe(1);
        body.GetProperty("definition").GetProperty("defaultTtl").GetString().ShouldBe("300s");
    }

    [RequiresDockerFact]
    public async Task Editing_an_open_draft_requires_the_current_entity_tag()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-2");
        (var application, _, var etag) = await ClassPolicyApi.CreateDraftAsync(author);

        HttpResponseMessage missing = await author.SendAsync(TemplateApi.PutJson(
            $"{ClassPolicyApi.PolicyUrl(application)}/draft",
            ClassPolicyApi.Definition(defaultTtl: "600s"),
            ifMatch: null));
        HttpResponseMessage stale = await author.SendAsync(TemplateApi.PutJson(
            $"{ClassPolicyApi.PolicyUrl(application)}/draft",
            ClassPolicyApi.Definition(defaultTtl: "600s"),
            ifMatch: "\"etag-antigo\""));
        HttpResponseMessage current = await author.SendAsync(TemplateApi.PutJson(
            $"{ClassPolicyApi.PolicyUrl(application)}/draft",
            ClassPolicyApi.Definition(defaultTtl: "600s"),
            etag));

        missing.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        current.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(current);
        body.GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("definition").GetProperty("defaultTtl").GetString().ShouldBe("600s");
    }

    [RequiresDockerFact]
    public async Task A_structurally_invalid_definition_returns_422_with_the_checks_and_saves_nothing()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-3");
        var application = ClassPolicyApi.NewApplication();
        var invalidDefinition = new
        {
            schemaVersion = 1,
            channelsAllowed = new[] { "push" },
            deliveryPlan = new object[] { new { channel = "sms" } },
            dedupeWindow = "60s",
        };

        HttpResponseMessage response = await author.SendAsync(TemplateApi.PutJson(
            $"{ClassPolicyApi.PolicyUrl(application)}/draft", invalidDefinition, ifMatch: null));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("class-policy-validation-failed");
        List<JsonElement> checks = [.. problem.GetProperty("checks").EnumerateArray()];
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "default-ttl"
            && check.GetProperty("status").GetString() == "failed");
        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "delivery-plan"
            && check.GetProperty("status").GetString() == "failed");
        HttpResponseMessage combined = await author.GetAsync(ClassPolicyApi.PolicyUrl(application));
        combined.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [RequiresDockerFact]
    public async Task The_author_cannot_publish_their_own_draft_even_with_the_publisher_role()
    {
        HttpClient authorPublisher = fixture.CreateClientWithToken(
            "author-cp-4",
            AuthorizationSetup.AuthorRole,
            AuthorizationSetup.PublisherRole);
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(authorPublisher);

        HttpResponseMessage response = await authorPublisher.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("four-eyes-violation");
        await fixture.ExecuteAuditDbAsync(async db =>
            (await db.Approvals.AsNoTracking().AnyAsync(candidate =>
                candidate.SubjectId == $"{application}:{ClassPolicyApi.DefaultClass}"))
                .ShouldBeFalse());
    }

    [RequiresDockerFact]
    public async Task A_distinct_publisher_publishes_recording_approval_and_audit_together()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-5");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-cp-5");
        (var application, var version, _) = await ClassPolicyApi.CreateDraftAsync(author);

        HttpResponseMessage response = await publisher.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("published");
        var contentHash = body.GetProperty("contentHash").GetString()!;
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            Approval approval = await db.Approvals.AsNoTracking().SingleAsync(candidate =>
                candidate.SubjectType == "class_policy_version"
                && candidate.SubjectId == $"{application}:{ClassPolicyApi.DefaultClass}"
                && candidate.SubjectVersion == version);
            approval.ContentHash.ShouldBe(contentHash);
            approval.ApproverOid.ShouldBe("publisher-cp-5");

            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "class_policy.version.published"
                && candidate.EntityId == $"{application}:{ClassPolicyApi.DefaultClass}:{version}");
            audit.ActorId.ShouldBe("publisher-cp-5");
            audit.EntityType.ShouldBe("class_policy_version");
            audit.Application.ShouldBe(application);
        });
    }

    [RequiresDockerFact]
    public async Task The_combined_view_returns_the_published_version_and_the_open_draft()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-6");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-cp-6");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        await ClassPolicyApi.PublishAsync(publisher, application);
        (_, var draftVersion, var draftEtag) = await ClassPolicyApi.CreateDraftAsync(
            author, application, definition: ClassPolicyApi.Definition(defaultTtl: "900s"));

        HttpResponseMessage response = await author.GetAsync(ClassPolicyApi.PolicyUrl(application));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.ETag!.ToString().ShouldBe(draftEtag);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("published").GetProperty("version").GetInt32().ShouldBe(1);
        body.GetProperty("published").GetProperty("status").GetString().ShouldBe("published");
        body.GetProperty("draft").GetProperty("version").GetInt32().ShouldBe(draftVersion);
        body.GetProperty("draft").GetProperty("definition").GetProperty("defaultTtl").GetString().ShouldBe("900s");
    }

    [RequiresDockerFact]
    public async Task Publishing_the_next_version_supersedes_the_previous_one()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-7");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-cp-7");
        (var application, var first, _) = await ClassPolicyApi.CreateDraftAsync(author);
        await ClassPolicyApi.PublishAsync(publisher, application);
        (_, var second, _) = await ClassPolicyApi.CreateDraftAsync(
            author, application, definition: ClassPolicyApi.Definition(dedupeWindow: "120s"));

        HttpResponseMessage response = await publisher.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("version").GetInt32().ShouldBe(second);
        body.GetProperty("supersededVersion").GetInt32().ShouldBe(first);
        HttpResponseMessage firstVersion = await author.GetAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/{first}");
        firstVersion.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await TemplateApi.ReadJsonAsync(firstVersion)).GetProperty("status").GetString().ShouldBe("superseded");
    }

    [RequiresDockerFact]
    public async Task The_definition_diff_reports_the_fields_that_changed_between_versions()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-8");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-cp-8");
        (var application, var first, _) = await ClassPolicyApi.CreateDraftAsync(author);
        await ClassPolicyApi.PublishAsync(publisher, application);
        (_, var second, _) = await ClassPolicyApi.CreateDraftAsync(
            author,
            application,
            definition: ClassPolicyApi.Definition(defaultTtl: "900s", consentPurpose: "marketing"));

        HttpResponseMessage response = await author.GetAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/{second}/diff?against={first}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        JsonElement definition = body.GetProperty("definition");
        List<string?> changed = [.. definition.GetProperty("changedFields").EnumerateArray().Select(field => field.GetString())];
        changed.ShouldContain("defaultTtl");
        List<string?> added = [.. definition.GetProperty("addedFields").EnumerateArray().Select(field => field.GetString())];
        added.ShouldContain("consentPurpose");
    }

    [RequiresDockerFact]
    public async Task A_class_outside_the_vocabulary_is_rejected_on_the_route()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-9");
        var application = ClassPolicyApi.NewApplication();

        HttpResponseMessage response = await author.SendAsync(TemplateApi.PutJson(
            $"{ClassPolicyApi.PolicyUrl(application, "marketing")}/draft",
            ClassPolicyApi.Definition(),
            ifMatch: null));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
        problem.GetProperty("detail").GetString()!.ShouldContain("marketing");
    }

    [RequiresDockerFact]
    public async Task An_application_and_class_without_any_policy_returns_404()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-10");

        HttpResponseMessage response = await author.GetAsync(
            ClassPolicyApi.PolicyUrl(ClassPolicyApi.NewApplication()));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TemplateApi.ReadJsonAsync(response)).GetProperty("type").GetString()
            .ShouldBe("class-policy-not-found");
    }

    [RequiresDockerFact]
    public async Task Publishing_without_an_open_draft_returns_404()
    {
        HttpClient author = fixture.CreateAuthorClient("author-cp-11");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-cp-11");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        await ClassPolicyApi.PublishAsync(publisher, application);

        HttpResponseMessage response = await publisher.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/publish", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await TemplateApi.ReadJsonAsync(response)).GetProperty("type").GetString()
            .ShouldBe("class-policy-draft-not-found");
    }
}
