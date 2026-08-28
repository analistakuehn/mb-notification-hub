using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.IntegrationTests.TemplateManagement;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class TemplateLifecycleEndpointTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Deprecating_an_active_template_records_the_reason_in_the_audit_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lc-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lc-1");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate",
            new { reason = "superseded-by-new-version", note = "substituído pela campanha nova" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("deprecated");
        HttpResponseMessage template = await author.GetAsync($"/v1/templates/{key}");
        (await TemplateApi.ReadJsonAsync(template)).GetProperty("status").GetString().ShouldBe("deprecated");

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "template.deprecated" && candidate.EntityId == key);
            audit.ActorId.ShouldBe("publisher-lc-1");
            audit.EntityType.ShouldBe("template");
            audit.DetailsJson.ShouldContain("substituído pela campanha nova");
        });
    }

    [RequiresDockerFact]
    public async Task Deprecating_without_a_reason_returns_400()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lc-2");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lc-2");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate", new { });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task Deprecating_twice_returns_409_with_the_remaining_transitions()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lc-3");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lc-3");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());
        (await publisher.PostAsJsonAsync($"/v1/templates/{key}/deprecate",
            new { reason = "superseded-by-new-version", note = "primeira" }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate", new { reason = "superseded-by-new-version", note = "segunda" });

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("invalid-state-transition");
        problem.GetProperty("currentStatus").GetString().ShouldBe("deprecated");
        List<string> allowed = [.. problem.GetProperty("allowedTransitions").EnumerateArray()
            .Select(transition => transition.GetString()!)];
        allowed.ShouldBe(["disabled"]);
    }

    [RequiresDockerFact]
    public async Task Disabling_a_deprecated_template_is_audited_as_its_own_event()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lc-4");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lc-4");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());
        (await publisher.PostAsJsonAsync($"/v1/templates/{key}/deprecate",
            new { reason = "retired", note = "aposentando" }))
            .EnsureSuccessStatusCode();

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable",
            new { reason = "content-incorrect", note = "conteúdo incorreto em produção" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonElement body = await TemplateApi.ReadJsonAsync(response);
        body.GetProperty("status").GetString().ShouldBe("disabled");
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "template.disabled" && candidate.EntityId == key);
            audit.DetailsJson.ShouldContain("conteúdo incorreto em produção");
        });
    }

    [RequiresDockerFact]
    public async Task An_author_only_token_cannot_deprecate()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lc-5");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        HttpResponseMessage response = await author.PostAsJsonAsync(
            $"/v1/templates/{key}/deprecate", new { reason = "retired", note = "sem papel de publicador" });

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [RequiresDockerFact]
    public async Task Deprecating_an_unknown_template_returns_404()
    {
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lc-6");

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{TemplateApi.NewKey()}/deprecate", new { reason = "retired", note = "não existe" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        JsonElement problem = await TemplateApi.ReadJsonAsync(response);
        problem.GetProperty("type").GetString().ShouldBe("template-not-found");
    }

    [RequiresDockerFact]
    public async Task Creating_a_template_writes_its_creation_to_the_audit_trail()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lc-7");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent audit = await db.AuditEvents.AsNoTracking().SingleAsync(candidate =>
                candidate.Action == "template.created" && candidate.EntityId == key);
            audit.ActorId.ShouldBe("author-lc-7");
            audit.ActorType.ShouldBe("user");
            audit.Application.ShouldBe("araia-cambio");
            audit.DetailsJson.ShouldContain("transactional");
        });
    }
}
