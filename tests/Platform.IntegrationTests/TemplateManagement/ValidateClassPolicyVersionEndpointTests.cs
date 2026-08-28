using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// On-demand validation of a stored class policy version. The draft endpoint
/// refuses a definition that does not pass, so every stored version passed the
/// catalog of the day it was written; this surface is what re-reads it against
/// the catalog of today, for a version nobody may edit anymore.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class ValidateClassPolicyVersionEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_published_version_returns_200_with_a_fully_passed_report()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-vcp-1");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        var version = await ClassPolicyApi.PublishAsync(publisher, application);

        HttpResponseMessage response = await author.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/{version}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("passed").GetBoolean().ShouldBeTrue();
        List<string> statuses = [.. body.GetProperty("checks").EnumerateArray()
            .Select(check => check.GetProperty("status").GetString()!)];
        statuses.ShouldNotBeEmpty();
        statuses.ShouldAllBe(status => status == "passed");
    }

    [RequiresDockerFact]
    public async Task A_stored_definition_that_no_longer_passes_comes_back_as_a_200_report_not_as_an_error()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-2");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-vcp-2");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        var published = await ClassPolicyApi.PublishAsync(publisher, application);
        var draft = await StoreVersionOutsideTheCatalogAsync(application, published + 1);

        HttpResponseMessage response = await author.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/{draft}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("passed").GetBoolean().ShouldBeFalse();
        List<JsonElement> checks = [.. body.GetProperty("checks").EnumerateArray()];

        JsonElement schema = checks.Single(check =>
            check.GetProperty("name").GetString() == "schema-version"
            && check.GetProperty("status").GetString() == "failed");
        schema.GetProperty("message").GetString()!.ShouldContain("7");
        schema.GetProperty("location").GetString().ShouldBe("schemaVersion");

        checks.ShouldContain(check =>
            check.GetProperty("name").GetString() == "channels-allowed"
            && check.GetProperty("status").GetString() == "failed");
    }

    [RequiresDockerFact]
    public async Task An_unknown_version_returns_404()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-3");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);

        HttpResponseMessage response = await author.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/99/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("class-policy-version-not-found");
    }

    [RequiresDockerFact]
    public async Task An_application_without_any_policy_returns_404()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-4");

        HttpResponseMessage response = await author.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(ClassPolicyApi.NewApplication())}/versions/1/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("class-policy-version-not-found");
    }

    [RequiresDockerFact]
    public async Task A_class_outside_the_vocabulary_is_rejected_on_the_route()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-5");
        var application = ClassPolicyApi.NewApplication();

        HttpResponseMessage response = await author.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application, "marketing")}/versions/1/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-request");
        problem.GetProperty("detail").GetString()!.ShouldContain("marketing");
    }

    [RequiresDockerFact]
    public async Task The_publisher_role_alone_does_not_reach_the_validation()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-6");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-vcp-6");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        var version = await ClassPolicyApi.PublishAsync(publisher, application);

        HttpResponseMessage response = await publisher.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/{version}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [RequiresDockerFact]
    public async Task Without_a_bearer_token_the_endpoint_returns_401()
    {
        HttpClient client = fixture.CreateClient();

        HttpResponseMessage response = await client.PostAsync(
            $"{ClassPolicyApi.PolicyUrl("qualquer-app")}/versions/1/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// The reason this surface exists: the trail keeps compact evidence of the
    /// publication, and the integral report has to be recoverable for the same
    /// version afterwards. The verdict and the catalog that produced it are
    /// what the row holds, so they are what the on-demand report must match.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_on_demand_report_matches_the_evidence_the_publication_recorded()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-7");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-vcp-7");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        var version = await ClassPolicyApi.PublishAsync(publisher, application);

        HttpResponseMessage response = await author.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/{version}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement report = await TemplateApi.ReadJsonAsync(response);
        List<JsonElement> checks = [.. report.GetProperty("checks").EnumerateArray()];
        checks.Count.ShouldBe(ExpectedCheckNames.Length);
        List<string> names = [.. checks
            .Select(check => check.GetProperty("name").GetString()!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        names.ShouldBe(ExpectedCheckNames);

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "class_policy.version.published"
                && candidate.EntityId == $"{application}:{ClassPolicyApi.DefaultClass}:{version}");
            using JsonDocument details = JsonDocument.Parse(audit.DetailsJson);
            JsonElement validation = details.RootElement.GetProperty("validation");
            validation.GetProperty("passed").GetBoolean()
                .ShouldBe(report.GetProperty("passed").GetBoolean());
            List<string> recorded = [.. validation.GetProperty("checks").EnumerateArray()
                .Select(name => name.GetString()!)];
            recorded.ShouldBe(names);
            validation.GetProperty("warnings").GetInt32().ShouldBe(checks.Count(check =>
                check.GetProperty("status").GetString() == "warning"));
        });
    }

    [RequiresDockerFact]
    public async Task Validating_writes_nothing_to_the_audit_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-vcp-8");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-vcp-8");
        (var application, _, _) = await ClassPolicyApi.CreateDraftAsync(author);
        var version = await ClassPolicyApi.PublishAsync(publisher, application);
        var before = 0;
        await fixture.ExecuteAuditDbAsync(async db => before = await db.AuditEvents.CountAsync());

        HttpResponseMessage response = await author.PostAsync(
            $"{ClassPolicyApi.PolicyUrl(application)}/versions/{version}/validate", content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        before.ShouldBeGreaterThan(0, "the publication above already wrote its own entry");
        await fixture.ExecuteAuditDbAsync(async db =>
            (await db.AuditEvents.CountAsync()).ShouldBe(before));
    }

    /// <summary>Every structural check of the version 1 vocabulary, ordinal order.</summary>
    private static readonly string[] ExpectedCheckNames =
    [
        "channels-allowed",
        "consent-purpose",
        "dedupe-window",
        "default-ttl",
        "definition-document",
        "delivery-plan",
        "quiet-hours",
        "schema-version",
    ];

    /// <summary>
    /// Stores a version the draft endpoint would refuse today. It stands in for
    /// a version written under an older catalog: the aggregate only requires a
    /// JSON object declaring an integer schema version, so this is the shape a
    /// stored version takes once the vocabulary moves past it.
    /// </summary>
    private async Task<int> StoreVersionOutsideTheCatalogAsync(string application, int version)
    {
        Result<ClassPolicyVersion> stored = ClassPolicyVersion.CreateDraft(new ClassPolicyDraftInput
        {
            Application = application,
            Class = NotificationClass.Transactional,
            Version = version,
            DefinitionJson = "{\"schemaVersion\":7,\"channelsAllowed\":[\"carrier-pigeon\"]}",
            CreatedBy = "author-vcp-2",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        stored.IsSuccess.ShouldBeTrue();
        await fixture.ExecuteDbAsync(async db =>
        {
            db.ClassPolicyVersions.Add(stored.Value!);
            await db.SaveChangesAsync();
        });
        return version;
    }
}
