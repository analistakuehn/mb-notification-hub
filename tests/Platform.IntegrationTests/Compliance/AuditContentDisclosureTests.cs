using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Dispatching;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Compliance;

/// <summary>
/// The single most sensitive read of the platform: opening what was rendered.
/// The template of these tests declares the access code as a sensitive variable,
/// so the two forms of the render differ and every claim about which one was
/// served is observable.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class AuditContentDisclosureTests(CorePipelineFixture fixture)
{
    private const string AccessCode = "123456";
    private const string Mask = "***";

    private static readonly string[] SensitiveCode = ["code"];

    [RequiresDockerFact]
    public async Task After_a_terminal_verdict_the_route_serves_the_masked_form_and_verifies_its_hash()
    {
        (Guid notificationId, _) = await SendAsync(runDispatch: true);
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var status, JsonElement body, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.ContentPath(notificationId, sequence: 1));

        status.ShouldBe(200);
        body.GetProperty("attemptStatus").GetString().ShouldBe("sent");

        // The form is named, not inferred, and it is the masked one.
        body.GetProperty("disclosedForm").GetString().ShouldBe("masked");
        body.GetProperty("completeFormStillStored").GetBoolean().ShouldBeFalse();

        // The masked hash is recomputed over exactly what was served and it
        // matches what the attempt recorded when it was queued.
        body.GetProperty("contentHashMaskedVerified").GetBoolean().ShouldBeTrue();
        body.GetProperty("recomputedContentHashMasked").GetString()
            .ShouldBe(body.GetProperty("contentHashMasked").GetString());

        // The complete hash travels declared, with no verification member: no
        // stored bytes reproduce it once the masking replaced the form.
        body.GetProperty("contentHashFull").GetString()
            .ShouldNotBe(body.GetProperty("contentHashMasked").GetString());
        body.TryGetProperty("contentHashFullVerified", out _).ShouldBeFalse();

        // What was served is the masked text, and the code is nowhere in it.
        body.GetProperty("body").GetString()!.ShouldContain(Mask);
        raw.ShouldNotContain(AccessCode);
    }

    [RequiresDockerFact]
    public async Task Before_a_terminal_verdict_the_route_still_serves_the_masked_form()
    {
        (Guid notificationId, _) = await SendAsync(runDispatch: false);
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var status, JsonElement body, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.ContentPath(notificationId, sequence: 1));

        status.ShouldBe(200);
        body.GetProperty("attemptStatus").GetString().ShouldBe("queued");

        // The store still holds the complete form, and the answer says so,
        // because the auditor must know which phase produced what they read.
        body.GetProperty("completeFormStillStored").GetBoolean().ShouldBeTrue();

        // What leaves is still the masked form: a disclosure surface that could
        // hand out a live one-time code would defeat the masking itself.
        body.GetProperty("disclosedForm").GetString().ShouldBe("masked");
        body.GetProperty("contentHashMaskedVerified").GetBoolean().ShouldBeTrue();
        body.GetProperty("body").GetString()!.ShouldContain(Mask);
        raw.ShouldNotContain(AccessCode);
    }

    [RequiresDockerFact]
    public async Task Opening_content_records_its_own_disclosure_with_the_form_and_the_hashes()
    {
        (Guid notificationId, _) = await SendAsync(runDispatch: true);
        var subject = notificationId.ToString();
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var status, JsonElement content, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.ContentPath(notificationId, sequence: 1));
        status.ShouldBe(200);
        (await AuditApi.CountDisclosuresAsync(fixture, AuditEntityTypes.Notification, subject))
            .ShouldBe(1);

        // Opening content discloses no recipient data, so it leaves no link on
        // the recipient subject.
        (_, JsonElement evidence, _) = await AuditApi.ReadAsync(
            auditor, AuditApi.EvidencePath(notificationId));
        JsonElement recorded = AuditApi.Items(evidence.GetProperty("trail"), "priorAccesses")
            .ShouldHaveSingleItem();

        JsonElement details = recorded.GetProperty("details");
        details.GetProperty("scope").GetString().ShouldBe("attempt-content");
        details.GetProperty("attemptSequence").GetInt32().ShouldBe(1);
        details.GetProperty("disclosedForm").GetString().ShouldBe("masked");
        details.GetProperty("contentHashVerified").GetBoolean().ShouldBeTrue();
        details.GetProperty("contentHashMasked").GetString()
            .ShouldBe(content.GetProperty("contentHashMasked").GetString());
        details.GetRawText().ShouldNotContain(AccessCode);
        details.GetRawText().ShouldNotContain(Mask);
    }

    [RequiresDockerFact]
    public async Task An_attempt_the_notification_does_not_have_answers_as_a_miss()
    {
        (Guid notificationId, _) = await SendAsync(runDispatch: true);
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var status, _, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.ContentPath(notificationId, sequence: 99));

        status.ShouldBe(404);
        raw.ShouldContain("audit-subject-not-found");
        (await AuditApi.CountDisclosuresAsync(
            fixture, AuditEntityTypes.Notification, notificationId.ToString()))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_sequence_below_the_first_attempt_is_a_bad_request()
    {
        (Guid notificationId, _) = await SendAsync(runDispatch: false);
        HttpClient auditor = fixture.CreateAuditorClient(AuditApi.AuditorSubject);

        (var status, _, var raw) = await AuditApi.ReadAsync(
            auditor, AuditApi.ContentPath(notificationId, sequence: 0));

        status.ShouldBe(400);
        raw.ShouldContain("invalid-request");
    }

    private async Task<(Guid NotificationId, string RecipientId)> SendAsync(bool runDispatch)
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates", SensitiveCode);
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");
        if (!runDispatch)
        {
            return (notificationId, recipientId);
        }

        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));
        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-audit-content" }));

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        return (notificationId, recipientId);
    }
}
