using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class CorePipelineRejectionTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_recipient_without_consent_ends_rejected_with_the_variables_purged()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", consentPurpose: "marketing");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        Guid notificationId = await ProcessOneAsync(application, templateKey, recipientId);

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Rejected);
        notification.VariablesEncrypted.ShouldBeNull();
        notification.PolicyVersion.ShouldNotBeNull();

        PolicyEvaluation rejection = await fixture.QueryNotificationsDbAsync(db => db.PolicyEvaluations
            .AsNoTracking()
            .SingleAsync(evaluation => evaluation.NotificationId == notificationId
                && evaluation.Result == PolicyEvaluationResults.Reject));
        rejection.Rule.ShouldBe("ConsentGate");
        rejection.Reason.ShouldBe("no-consent");

        await AssertNoAttemptAndAuditedAsync(notificationId, "notification.rejected");
    }

    [RequiresDockerFact]
    public async Task A_second_request_inside_the_dedupe_window_ends_rejected_as_a_duplicate()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await CorePipelineApi.CreatePublishedPolicyAsync(fixture, application, "transactional");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        Guid firstId = await ProcessOneAsync(application, templateKey, recipientId);
        Guid secondId = await ProcessOneAsync(application, templateKey, recipientId);

        (await StatusAsync(firstId)).ShouldBe(NotificationStatuses.Dispatched);
        (await StatusAsync(secondId)).ShouldBe(NotificationStatuses.Rejected);

        PolicyEvaluation rejection = await fixture.QueryNotificationsDbAsync(db => db.PolicyEvaluations
            .AsNoTracking()
            .SingleAsync(evaluation => evaluation.NotificationId == secondId
                && evaluation.Result == PolicyEvaluationResults.Reject));
        rejection.Rule.ShouldBe("DedupeWindow");
        rejection.Reason.ShouldBe("duplicate-window");

        await AssertNoAttemptAndAuditedAsync(secondId, "notification.rejected");
    }

    [RequiresDockerFact]
    public async Task A_recipient_without_any_reachable_channel_ends_rejected_as_no_valid_contact()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await CorePipelineApi.CreatePublishedPolicyAsync(fixture, application, "transactional");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(
            fixture, withSmsContact: false, withDevice: false);

        Guid notificationId = await ProcessOneAsync(application, templateKey, recipientId);

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Rejected);
        notification.VariablesEncrypted.ShouldBeNull();

        PolicyEvaluation rejection = await fixture.QueryNotificationsDbAsync(db => db.PolicyEvaluations
            .AsNoTracking()
            .SingleAsync(evaluation => evaluation.NotificationId == notificationId
                && evaluation.Result == PolicyEvaluationResults.Reject));
        rejection.Rule.ShouldBe("ChannelSelection");
        rejection.Reason.ShouldBe("no-valid-contact");

        await AssertNoAttemptAndAuditedAsync(notificationId, "notification.rejected");
    }

    [RequiresDockerFact]
    public async Task A_quiet_hours_window_defers_the_notification_and_parks_it()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");

        // A window that certainly covers this instant in the recipient's zone.
        var saoPaulo = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
        DateTimeOffset localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, saoPaulo);
        var from = localNow.AddHours(-1).ToString("HH:mm", CultureInfo.InvariantCulture);
        var to = localNow.AddHours(1).ToString("HH:mm", CultureInfo.InvariantCulture);
        await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", quietHours: new { from, to });
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        Guid notificationId = await ProcessOneAsync(application, templateKey, recipientId);

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Deferred);
        notification.ReleaseAt.ShouldNotBeNull();
        notification.ReleaseAt.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        // The variables stay sealed: the pipeline resumes from here later.
        notification.VariablesEncrypted.ShouldNotBeNull();

        // Parked: no attempt, no dispatch outbox row, and the deferral audited.
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(attempt => attempt.NotificationId == notificationId)))
            .ShouldBe(0);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "notification.deferred"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);
    }

    private async Task<Guid> ProcessOneAsync(string application, string templateKey, string recipientId)
    {
        HttpClient producer = fixture.CreateProducerClient(
            "billing-service", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(application, templateKey, "transactional", recipientId),
            Guid.NewGuid().ToString("N"));
        accepted.EnsureSuccessStatusCode();
        JsonElement body = await NotificationsApi.ReadJsonAsync(accepted);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid id).ShouldBeTrue();

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        await CorePipelineFixture.RunCorePassAsync(worker, "core-transactional");
        return id;
    }

    private async Task<string> StatusAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.Status)
            .SingleAsync());

    private async Task AssertNoAttemptAndAuditedAsync(Guid notificationId, string action)
    {
        (await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .CountAsync(attempt => attempt.NotificationId == notificationId)))
            .ShouldBe(0);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == action
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);
    }
}
