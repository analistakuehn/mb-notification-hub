using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.Dispatch;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Dispatching;

/// <summary>
/// The two result events the dispatch side owns. Both are written inside the
/// transaction of the verdict they report and both go in before the audit
/// append, because the append holds the chain lock of its partition until the
/// transaction ends.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class DispatchEventEmissionTests(CorePipelineFixture fixture)
{
    private const string DeliveredType = "araia.notification.delivered.v1";
    private const string FailedType = "araia.notification.failed.v1";

    private const string InvalidArgumentBody = """
        {"error":{"code":400,"message":"Invalid argument.","status":"INVALID_ARGUMENT",
        "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"INVALID_ARGUMENT"}]}}
        """;

    [RequiresDockerFact]
    public async Task A_push_acceptance_on_the_last_step_emits_the_delivered_event_before_it_appends_the_trail()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        // A single step, so the acceptance carries no fallback deadline and is
        // the strongest signal this hub will ever hold about the message. The
        // test is about the order of the two writes, and only the last step
        // still produces the delivered event at acceptance time.
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:1"}""", null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        AppendOrderProbe? probe = null;
        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress),
            replaceServices: AuditTrailDecoration.ProbeAppendOrder(captured => probe = captured));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Delivered);

        OutboxMessage published = await BusEventAsync(recipientId, DeliveredType);
        published.Transport.ShouldBe(OutboxTransports.Kafka);
        published.Destination.ShouldBe("notifications.events.v1");
        // The class of the notification, never the auth band: the auth band
        // protects the delivery latency of a code, and a result is not a
        // delivery.
        published.PriorityClass.ShouldBe("critical");

        CloudEvent envelope = ParseEnvelope(published);
        envelope.Subject.ShouldBe(recipientId);
        envelope.Data.GetProperty("channel").GetString().ShouldBe("push");
        envelope.Data.GetProperty("notificationId").GetString().ShouldBe(notificationId.ToString());

        probe.ShouldNotBeNull();
        probe.BusRowsBeforeAuditOf(notificationId.ToString()).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_push_acceptance_on_a_step_with_a_deadline_announces_no_delivery()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:1"}""", null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The attempt is sent and the notification is still open: a later step
        // exists, so acceptance is not an answer about delivery and announcing
        // one would tell the producer something this hub does not know.
        (await AttemptStatusAsync(notificationId)).ShouldBe(NotificationAttemptStatuses.Sent);
        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Dispatched);
        (await CountBusEventsAsync(recipientId, DeliveredType)).ShouldBe(0);
        (await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(auditEvent => auditEvent.Action == "notification.delivered"
                && auditEvent.EntityId == notificationId.ToString())))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task An_exhausted_plan_emits_the_failure_event_with_the_last_channel()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        // A single step: its failure exhausts the plan straight away.
        await DispatchApi.CreatePublishedPolicyAsync(fixture, application, "critical", ("push", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(fixture, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(400, InvalidArgumentBody, null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Failed);

        OutboxMessage published = await BusEventAsync(recipientId, FailedType);
        published.Transport.ShouldBe(OutboxTransports.Kafka);
        CloudEvent envelope = ParseEnvelope(published);
        envelope.Data.GetProperty("lastChannel").GetString().ShouldBe("push");
        envelope.Data.GetProperty("notificationId").GetString().ShouldBe(notificationId.ToString());
        envelope.Data.GetProperty("reason").GetString().ShouldNotBeNullOrEmpty();
    }

    /// <summary>
    /// The other way a plan ends: not at the dispatcher, where the verdict of
    /// the last step concludes it, but at the Core, when the trigger of a
    /// step with a deadline arrives and the admitted plan has no usable step
    /// after it. Here the admission dropped e-mail because the recipient has
    /// no address, so the only step still carries a deadline and its failure
    /// asks the Core for a next step that does not exist. That conclusion
    /// must reach the producer like every other one.
    /// </summary>
    [RequiresDockerFact]
    public async Task A_plan_with_no_usable_next_step_at_the_fallback_emits_the_failure_event()
    {
        var application = DispatchApi.NewApplication();
        (var templateKey, _) = await DispatchApi.CreatePublishedTemplateAsync(
            fixture, application, "critical", "authentication");
        await DispatchApi.CreatePublishedPolicyAsync(
            fixture, application, "critical", ("push", "30s"), ("email", null));
        (var recipientId, _, _) = await DispatchApi.RegisterRecipientAsync(
            fixture, withEmail: false, deviceCount: 1);
        await fixture.SeedProviderConfigAsync(("email", "sendgrid"), ("push", "fcm"));

        await using FakeProviderServer provider = await FakeProviderServer.StartAsync();
        provider.Handler = request => Task.FromResult(request.Path == DispatchApi.FcmTokenPath
            ? new FakeProviderResponse(200, DispatchApi.FcmTokenBody, null)
            : new FakeProviderResponse(400, InvalidArgumentBody, null));

        Guid notificationId = await DispatchApi.AcceptAndRouteAsync(
            fixture, application, templateKey, "critical", recipientId, "core-auth");

        await using ServiceProvider dispatcher = fixture.BuildDispatcherWorkerProvider(
            DispatchApi.ProviderSettings(provider.BaseAddress, provider.BaseAddress));
        (await CorePipelineFixture.RunDispatchPassAsync(dispatcher, "dispatch-push-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        // The refusal wrote the fallback trigger, not the conclusion: the step
        // had a deadline, so the dispatcher's own path treats it as one with
        // a successor. The Core is the one that finds out there is none.
        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Dispatched);
        (await CountBusEventsAsync(recipientId, FailedType)).ShouldBe(0);

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);
        await using ServiceProvider core = fixture.BuildCoreWorkerProvider();
        (await CorePipelineFixture.RunCorePassAsync(core, "core-auth"))
            .Processed.ShouldBeGreaterThanOrEqualTo(1);

        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Failed);

        OutboxMessage published = await BusEventAsync(recipientId, FailedType);
        published.Transport.ShouldBe(OutboxTransports.Kafka);
        CloudEvent envelope = ParseEnvelope(published);
        envelope.Data.GetProperty("reason").GetString().ShouldBe("plan-exhausted");
        envelope.Data.GetProperty("lastChannel").GetString().ShouldBe("push");
        envelope.Data.GetProperty("notificationId").GetString().ShouldBe(notificationId.ToString());
    }

    private async Task<OutboxMessage> BusEventAsync(string recipientId, string eventType)
        => await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.EventType == eventType
                && message.MessageKey == recipientId));

    private static CloudEvent ParseEnvelope(OutboxMessage message)
    {
        CloudEventParse parse = CloudEventParser.Parse(message.PayloadJson);
        parse.InvalidReason.ShouldBeNull();
        return parse.Event!;
    }

    private async Task<int> CountBusEventsAsync(string recipientId, string eventType)
        => await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.EventType == eventType
                && message.MessageKey == recipientId));

    private async Task<string> AttemptStatusAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.NotificationId == notificationId)
            .Select(attempt => attempt.Status)
            .SingleAsync());

    private async Task<string> StatusAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());
}
