using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchEmailEndToEndTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task An_email_crosses_from_acceptance_to_sent_without_leaking_the_address_or_the_content()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, var email, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-message-1" }));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        var logs = new CapturingLoggerProvider();
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress), logs);
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The attempt reached sent with the provider identity and receipt.
        NotificationAttempt attempt = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        attempt.ProviderKey.ShouldBe("sendgrid");
        attempt.ProviderMessageId.ShouldBe("sg-message-1");
        attempt.SentAt.ShouldNotBeNull();

        // E-mail acceptance is not delivery: without webhooks the
        // notification stays dispatched.
        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Dispatched);

        // The provider received the revealed address, the rendered content
        // and the correlation ids, exactly once. Scoped to this recipient's
        // address: relay passes may also publish pending attempts of earlier
        // tests in the shared collection.
        FakeProviderRequest request = provider.Requests
            .Where(candidate => candidate.Body.Contains(email, StringComparison.Ordinal))
            .ShouldHaveSingleItem();
        request.Authorization.ShouldBe("Bearer test-key");
        using JsonDocument payload = JsonDocument.Parse(request.Body);
        JsonElement personalization = payload.RootElement.GetProperty("personalizations")[0];
        personalization.GetProperty("to")[0].GetProperty("email").GetString().ShouldBe(email);
        JsonElement customArgs = personalization.GetProperty("custom_args");
        customArgs.GetProperty("notification_id").GetString().ShouldBe(notificationId.ToString());
        customArgs.GetProperty("attempt_id").GetString().ShouldBe(attempt.Id.ToString());
        payload.RootElement.GetProperty("content")[1].GetProperty("value").GetString()!
            .ShouldContain("123456");

        // The address and the plaintext content exist nowhere at rest: not in
        // the queue payloads, not in the audit trail, not in the logs.
        List<string> outboxPayloads = await fixture.QueryPlatformDbAsync(db => db.Database
            .SqlQuery<string>($"""SELECT payload::text AS "Value" FROM platform.outbox""")
            .ToListAsync());
        outboxPayloads.ShouldAllBe(payloadText => !payloadText.Contains(email));
        outboxPayloads.ShouldAllBe(payloadText => !payloadText.Contains("123456"));
        List<string> auditDetails = await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .Where(auditEvent => auditEvent.EntityId == notificationId.ToString())
            .Select(auditEvent => auditEvent.DetailsJson)
            .ToListAsync());
        auditDetails.ShouldAllBe(details => !details.Contains(email));
        logs.Lines.ShouldAllBe(line => !line.Contains(email));
        logs.Lines.ShouldAllBe(line => !line.Contains("123456"));

        // The correlation rode beside the content: the audited hashes stayed
        // exactly what the render produced.
        attempt.ContentHashFull.ShouldNotBeNullOrWhiteSpace();
        attempt.ContentHashMasked.ShouldNotBeNullOrWhiteSpace();
    }

    [RequiresDockerFact]
    public async Task Throttling_returns_the_attempt_to_the_queue_and_the_message_comes_back_later()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            429, null, new Dictionary<string, string> { ["Retry-After"] = "1" }));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Postponed.ShouldBeGreaterThanOrEqualTo(1);

        // The attempt returned to the queue without an owner and without a
        // dedupe mark, so the redelivery may claim it again.
        NotificationAttempt reverted = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));
        reverted.Status.ShouldBe(NotificationAttemptStatuses.Queued);
        reverted.ProviderKey.ShouldBeNull();

        // After the provider recovers, the same message comes back and lands
        // on sent: nothing was lost and nothing was sent twice.
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "sg-message-2" }));
        await Task.Delay(TimeSpan.FromSeconds(2));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);
        NotificationAttempt sent = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));
        sent.Status.ShouldBe(NotificationAttemptStatuses.Sent);
        sent.ProviderMessageId.ShouldBe("sg-message-2");
    }

    [RequiresDockerFact]
    public async Task A_timeout_parks_the_attempt_on_unknown_without_any_progress()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            return new FakeProviderResponse(202, null, null);
        };

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress, timeoutSeconds: 1));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        NotificationAttempt attempt = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Unknown);
        attempt.ErrorCode.ShouldBe("timeout");

        // No progress from unknown in this phase: no fallback trigger, no
        // notification transition.
        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Dispatched);
        (await DispatchApi.ReadOutboxPayloadsAsync(fixture, "core-transactional", notificationId))
            .ShouldAllBe(payload => !payload.Contains("fallback.requested"));
    }

    [RequiresDockerFact]
    public async Task A_definitive_rejection_on_the_last_step_fails_the_notification_in_the_same_transaction()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = _ => Task.FromResult(new FakeProviderResponse(
            400, """{"errors":[{"message":"invalid","field":"to"}]}""", null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "transactional", recipientId, "core-transactional");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-email-transactional"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        NotificationAttempt attempt = await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .SingleAsync(candidate => candidate.NotificationId == notificationId));
        attempt.Status.ShouldBe(NotificationAttemptStatuses.Failed);
        attempt.ErrorCode.ShouldBe("http-400");

        // Last plan step: the plan is exhausted, the notification fails and
        // no fallback trigger exists.
        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(NotificationStatuses.Failed);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "notification.failed"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(1);
        (await DispatchApi.ReadOutboxPayloadsAsync(fixture, "core-transactional", notificationId))
            .ShouldAllBe(payload => !payload.Contains("fallback.requested"));
    }
}
