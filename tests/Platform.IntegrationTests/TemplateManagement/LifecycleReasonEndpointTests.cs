using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Why a template or a layout left circulation, as the trail records it. The
/// periodic evidence report groups the trail by this field and copies the
/// group name into an archived document, so what lands here is a code plus an
/// optional note, never a sentence standing in for a category.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class LifecycleReasonEndpointTests(TemplateManagementApiFixture fixture)
{
    private const string Note = "OTP saindo com o nome do produto errado";

    [RequiresDockerFact]
    public async Task Disabling_a_template_records_a_canonical_reason_code()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lr-1");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lr-1");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable",
            new { reason = LifecycleReasons.ContentIncorrect, note = Note });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            JsonElement details = await DetailsOfAsync(db, "template.disabled", key);
            var reason = details.GetProperty("reason").GetString();
            LifecycleReasons.IsCanonical(reason).ShouldBeTrue(reason);
            reason.ShouldBe(LifecycleReasons.ContentIncorrect);
            details.GetProperty("note").GetString().ShouldBe(Note);
        });
    }

    [RequiresDockerFact]
    public async Task Disabling_a_template_with_the_other_reason_and_no_note_is_refused()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lr-2");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lr-2");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable", new { reason = LifecycleReasons.Other });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        await fixture.ExecuteAuditDbAsync(async db =>
            (await db.AuditEvents.AsNoTracking().AnyAsync(candidate =>
                candidate.Action == "template.disabled" && candidate.EntityId == key)).ShouldBeFalse());
    }

    /// <summary>
    /// The refusal that makes the vocabulary load-bearing. Without it the
    /// canonical code above would pass on an endpoint that still accepts any
    /// sentence, because a canonical value is also a valid free-text value.
    /// </summary>
    [RequiresDockerFact]
    public async Task Disabling_a_template_with_a_reason_outside_the_vocabulary_is_refused()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lr-3");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lr-3");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        HttpResponseMessage response = await publisher.PostAsJsonAsync(
            $"/v1/templates/{key}/disable", new { reason = "conteúdo incorreto em produção" });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [RequiresDockerFact]
    public async Task Deprecating_a_layout_records_the_same_reason_shape_as_disabling_a_template()
    {
        HttpClient author = fixture.CreateAuthorClient("author-lr-4");
        HttpClient publisher = fixture.CreatePublisherClient("publisher-lr-4");
        var templateKey = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());
        var layoutKey = await LayoutApi.CreateLayoutAsync(author, LayoutApi.NewKey());

        (await publisher.PostAsJsonAsync(
                $"/v1/templates/{templateKey}/disable",
                new { reason = LifecycleReasons.ContentCompromised, note = Note }))
            .EnsureSuccessStatusCode();
        (await publisher.PostAsJsonAsync(
                $"/v1/layouts/{layoutKey}/deprecate",
                new { reason = LifecycleReasons.VisualIdentityChange, note = Note }))
            .EnsureSuccessStatusCode();

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            JsonElement disabled = await DetailsOfAsync(db, "template.disabled", templateKey);
            JsonElement deprecated = await DetailsOfAsync(db, "layout.deprecated", layoutKey);

            Names(deprecated).ShouldBe(Names(disabled), ignoreOrder: true);
            Names(deprecated).ShouldBe(["note", "reason"], ignoreOrder: true);
            LifecycleReasons.IsCanonical(deprecated.GetProperty("reason").GetString()).ShouldBeTrue();
        });
    }

    private static async Task<JsonElement> DetailsOfAsync(AuditDbContext db, string action, string entityId)
    {
        AuditEvent audit = await db.AuditEvents.AsNoTracking()
            .SingleAsync(candidate => candidate.Action == action && candidate.EntityId == entityId);
        using var document = JsonDocument.Parse(audit.DetailsJson);
        return document.RootElement.Clone();
    }

    private static List<string> Names(JsonElement element)
        => [.. element.EnumerateObject().Select(property => property.Name)];
}
