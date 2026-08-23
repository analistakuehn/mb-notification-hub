using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>
/// The outgoing result events of the lifecycle. Each one is written inside the
/// transaction of the effect it reports, through the outbox, so an event
/// exists if and only if what it announces really happened; and each one is
/// appended before the audit trail, because the audit append holds the chain
/// lock of its partition until the transaction ends.
/// </summary>
[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class NotificationEventEmissionTests(CorePipelineFixture fixture)
{
    private const string BusTopic = "notifications.events.v1";
    private const string RejectedType = "araia.notification.rejected.v1";
    private const string FailedType = "araia.notification.failed.v1";

    [RequiresDockerFact]
    public async Task A_pipeline_rejection_emits_the_rejection_event_before_it_appends_the_trail()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await CorePipelineApi.CreatePublishedPolicyAsync(
            fixture, application, "transactional", consentPurpose: "marketing");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        AppendOrderProbe? probe = null;
        Guid notificationId = await ProcessOneAsync(
            application, templateKey, recipientId,
            AuditTrailDecoration.ProbeAppendOrder(captured => probe = captured));

        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Rejected);

        OutboxMessage published = await BusEventAsync(recipientId, RejectedType);
        published.Destination.ShouldBe(BusTopic);
        published.Transport.ShouldBe(OutboxTransports.Kafka);
        published.MessageKey.ShouldBe(recipientId);
        published.PriorityClass.ShouldBe("transactional");

        CloudEvent envelope = ParseEnvelope(published);
        envelope.Subject.ShouldBe(recipientId);
        envelope.Data.GetProperty("notificationId").GetString().ShouldBe(notificationId.ToString());
        envelope.Data.GetProperty("reason").GetString().ShouldBe("no-consent");
        envelope.Data.GetProperty("templateKey").GetString().ShouldBe(templateKey);

        // The bus row was already visible to the transaction when the audit
        // append ran, which is exactly what "before the trail" means here.
        probe.ShouldNotBeNull();
        probe.BusRowsBeforeAuditOf(notificationId.ToString()).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task An_expired_notification_emits_a_failure_event_with_the_expiry_reason()
    {
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await CorePipelineApi.CreatePublishedPolicyAsync(fixture, application, "transactional");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        // A time to live of one second, consumed after it elapsed.
        Guid notificationId = await ProcessOneAsync(
            application, templateKey, recipientId, replaceServices: null, ttlSeconds: 1,
            beforeConsume: () => Task.Delay(TimeSpan.FromSeconds(2)));

        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Expired);

        OutboxMessage published = await BusEventAsync(recipientId, FailedType);
        published.Transport.ShouldBe(OutboxTransports.Kafka);
        CloudEvent envelope = ParseEnvelope(published);
        envelope.Data.GetProperty("reason").GetString().ShouldBe("expired");
        envelope.Data.GetProperty("notificationId").GetString().ShouldBe(notificationId.ToString());
    }

    [RequiresDockerFact]
    public async Task A_dispatched_notification_appends_its_trail_with_no_bus_row_ahead_of_it()
    {
        // Falsification of the order probe: an outcome with no outgoing event
        // must record zero rows before the audit call. Without this, the
        // assertion of the rejection test would hold for a probe that counted
        // anything at all.
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        await CorePipelineApi.CreatePublishedPolicyAsync(fixture, application, "transactional");
        var recipientId = await CorePipelineApi.RegisterRecipientAsync(fixture);

        AppendOrderProbe? probe = null;
        Guid notificationId = await ProcessOneAsync(
            application, templateKey, recipientId,
            AuditTrailDecoration.ProbeAppendOrder(captured => probe = captured));

        (await StatusAsync(notificationId)).ShouldBe(NotificationStatuses.Dispatched);
        probe.ShouldNotBeNull();
        probe.BusRowsBeforeAuditOf(notificationId.ToString()).ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task An_accepted_request_announces_nothing_on_the_bus()
    {
        // Falsification of the emission: the producer already holds its
        // acceptance, so an acceptance event would be noise, and a test that
        // never checked this would pass with an emitter that fires on every
        // outcome.
        var application = CorePipelineApi.NewApplication();
        (var templateKey, _) = await CorePipelineApi.CreatePublishedTemplateAsync(
            fixture, application, "transactional", "order-updates");
        var recipientId = $"cus_{Guid.NewGuid():N}";

        HttpClient producer = fixture.CreateProducerClient(
            "billing-service", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(application, templateKey, "transactional", recipientId),
            Guid.NewGuid().ToString("N"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.MessageKey == recipientId
                && message.Transport == OutboxTransports.Kafka)))
            .ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_rejection_at_ingestion_emits_an_event_without_a_notification_identifier()
    {
        var application = CorePipelineApi.NewApplication();
        var recipientId = $"cus_{Guid.NewGuid():N}";
        HttpClient producer = fixture.CreateProducerClient(
            "billing-service", NotificationsApi.SendTransactional);

        HttpResponseMessage rejected = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(
                application, "template-that-was-never-published", "transactional", recipientId),
            Guid.NewGuid().ToString("N"));
        rejected.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        OutboxMessage published = await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .SingleAsync(message => message.MessageKey == recipientId
                && message.Transport == OutboxTransports.Kafka));
        CloudEvent envelope = ParseEnvelope(published);

        // No notification row exists when the ingestion refuses, so the event
        // carries the idempotency key as the correlation the producer holds.
        envelope.Data.TryGetProperty("notificationId", out _).ShouldBeFalse();
        envelope.Data.GetProperty("idempotencyKey").GetString().ShouldNotBeNullOrEmpty();
        envelope.Data.GetProperty("reason").GetString().ShouldBe("template-not-found");
    }

    /// <summary>
    /// The published row of one event type for one recipient. Keyed by the
    /// record key on purpose: the payload column is jsonb, so a text match
    /// over it never reaches the database as a comparison.
    /// </summary>
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

    private async Task<string> StatusAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());

    private async Task<Guid> ProcessOneAsync(
        string application,
        string templateKey,
        string recipientId,
        Action<IServiceCollection>? replaceServices = null,
        int ttlSeconds = 300,
        Func<Task>? beforeConsume = null)
    {
        HttpClient producer = fixture.CreateProducerClient(
            "billing-service", NotificationsApi.SendTransactional);
        HttpResponseMessage accepted = await NotificationsApi.PostNotificationAsync(
            producer,
            CorePipelineApi.NotificationBody(
                application, templateKey, "transactional", recipientId, ttlSeconds),
            Guid.NewGuid().ToString("N"));
        accepted.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        Guid notificationId = await ReadNotificationIdAsync(accepted);

        await using ServiceProvider relay = fixture.BuildRelayProvider();
        await CorePipelineFixture.RunRelayPassAsync(relay);

        if (beforeConsume is not null)
        {
            await beforeConsume();
        }

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider(
            overrides: null, replaceServices: replaceServices);
        await CorePipelineFixture.RunCorePassAsync(worker, "core-transactional");
        return notificationId;
    }

    private static async Task<Guid> ReadNotificationIdAsync(HttpResponseMessage response)
    {
        JsonElement body = await NotificationsApi.ReadJsonAsync(response);
        NotificationId.TryParse(body.GetProperty("notificationId").GetString(), out Guid id).ShouldBeTrue();
        return id;
    }
}
