using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.DeliveryTracking;

[Collection(DeliveryTrackingCollectionDefinition.Name)]
public sealed class DeliveryStateApplicationTests(DeliveryTrackingFixture fixture)
{
    private const string AppliedAction = "delivery.event_applied";

    [RequiresDockerFact]
    public async Task A_confirmation_moves_an_email_attempt_from_sent_to_delivered_and_stamps_the_instant()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, $"msg-{Guid.NewGuid():N}");
        var eventId = $"evt-{Guid.NewGuid():N}";
        var occurredAt = DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds();

        HttpResponseMessage response = await fixture.CreateClient().SendAsync(
            DeliveryTrackingApi.SendGridCallback(
                fixture,
                DeliveryEventBatch.Of(
                    eventId, $"msg-{Guid.NewGuid():N}", "delivered", seeded, occurredAt: occurredAt)));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Delivered);

        DateTimeOffset? deliveredAt = await DeliveryTrackingApi.ReadAttemptDeliveredAtAsync(
            fixture, seeded.AttemptId);
        deliveredAt.ShouldNotBeNull();

        // The provider's instant, never the instant the feedback was consumed:
        // the stamp answers when the message arrived.
        deliveredAt.Value.ToUnixTimeSeconds().ShouldBe(occurredAt);
        (await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId)).AppliedAt.ShouldNotBeNull();
    }

    [RequiresDockerFact]
    public async Task A_definitive_bounce_moves_the_attempt_to_bounced()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, $"msg-{Guid.NewGuid():N}");
        var eventId = $"evt-{Guid.NewGuid():N}";

        HttpResponseMessage response = await fixture.CreateClient().SendAsync(
            DeliveryTrackingApi.SendGridCallback(
                fixture,
                DeliveryEventBatch.Bounce(eventId, $"msg-{Guid.NewGuid():N}", seeded, "bounce")));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Bounced);
        (await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId)).ErrorCode.ShouldNotBeNull();
    }

    [RequiresDockerFact]
    public async Task Feedback_that_echoes_no_correlation_finds_the_attempt_by_the_provider_message_id()
    {
        var messageSid = $"SM{Guid.NewGuid():N}";
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "sms", DeliveryTrackingApi.TwilioProvider, messageSid);
        List<KeyValuePair<string, string>> form =
        [
            new("MessageSid", messageSid),
            new("MessageStatus", "delivered"),
        ];

        // No query correlation and no correlation in the body: the provider
        // message identity is the only route left.
        HttpResponseMessage response = await fixture.CreateClient()
            .SendAsync(DeliveryTrackingApi.TwilioCallback(form));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Delivered);

        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(
            fixture, $"{messageSid}:delivered");
        evidence.AttemptId.ShouldBe(
            seeded.AttemptId,
            "a aplicação precisa gravar de volta a correlação que resolveu, "
            + "senão a evidência nunca é legível por notificação.");
        evidence.NotificationId.ShouldBe(seeded.NotificationId);
    }

    [RequiresDockerFact]
    public async Task The_route_correlation_fills_feedback_that_arrives_without_one()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "sms", DeliveryTrackingApi.TwilioProvider, providerMessageId: null);
        var messageSid = $"SM{Guid.NewGuid():N}";
        List<KeyValuePair<string, string>> form =
        [
            new("MessageSid", messageSid),
            new("MessageStatus", "delivered"),
        ];
        var query = $"?notificationId={seeded.NotificationId}&attemptId={seeded.AttemptId}";

        HttpResponseMessage response = await fixture.CreateClient()
            .SendAsync(DeliveryTrackingApi.TwilioCallback(form, query));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // Correlation is knowledge of this module, and the callback address is
        // this module's: the attempt carries no provider message identity, so
        // without the route parameters the feedback would never find it.
        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(
            fixture, $"{messageSid}:delivered");
        evidence.AttemptId.ShouldBe(seeded.AttemptId);

        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Delivered);
    }

    [RequiresDockerFact]
    public async Task Feedback_about_an_attempt_nobody_knows_stays_stored_and_unapplied()
    {
        var eventId = $"evt-{Guid.NewGuid():N}";
        var unknown = new SeededAttempt(Guid.CreateVersion7(), Guid.CreateVersion7(), "app-unknown");

        HttpResponseMessage response = await fixture.CreateClient().SendAsync(
            DeliveryTrackingApi.SendGridCallback(
                fixture,
                DeliveryEventBatch.Of(eventId, $"msg-{Guid.NewGuid():N}", "delivered", unknown)));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        // A provider may call back before the transaction that recorded the
        // send commits, so this is an ordinary case and never a failure.
        await ApplyAsync();

        EvidenceRow evidence = await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId);
        evidence.AppliedAt.ShouldBeNull(
            "o evento de attempt desconhecido fica armazenado e não aplicado.");
        (await CountTrailAsync(unknown.NotificationId)).ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_transition_that_is_not_valid_from_the_stored_status_is_recorded_and_ignored()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture,
            "email",
            DeliveryTrackingApi.SendGridProvider,
            $"msg-{Guid.NewGuid():N}",
            status: NotificationAttemptStatuses.Failed);
        var eventId = $"evt-{Guid.NewGuid():N}";

        HttpResponseMessage response = await fixture.CreateClient().SendAsync(
            DeliveryTrackingApi.SendGridCallback(
                fixture,
                DeliveryEventBatch.Of(eventId, $"msg-{Guid.NewGuid():N}", "delivered", seeded)));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId)).ShouldBe(
            NotificationAttemptStatuses.Failed,
            "uma confirmação nunca ressuscita um attempt que já falhou.");
        (await DeliveryTrackingApi.ReadEvidenceAsync(fixture, eventId)).AppliedAt.ShouldBeNull();
        (await CountTrailAsync(seeded.NotificationId)).ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task The_application_writes_the_trail_the_receiving_request_deliberately_did_not()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, $"msg-{Guid.NewGuid():N}");
        var eventId = $"evt-{Guid.NewGuid():N}";

        HttpResponseMessage response = await fixture.CreateClient().SendAsync(
            DeliveryTrackingApi.SendGridCallback(
                fixture,
                DeliveryEventBatch.Of(eventId, $"msg-{Guid.NewGuid():N}", "delivered", seeded)));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        (await CountTrailAsync(seeded.NotificationId)).ShouldBe(
            0, "a requisição que recebe o callback não escreve trilha.");

        await ApplyAsync();

        (await CountTrailAsync(seeded.NotificationId)).ShouldBe(
            1, "a trilha é escrita pelo consumidor assíncrono, com a transição que ele aplicou.");
    }

    [RequiresDockerFact]
    public async Task A_confirmation_applied_twice_moves_the_attempt_once()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, $"msg-{Guid.NewGuid():N}");
        var eventId = $"evt-{Guid.NewGuid():N}";

        HttpResponseMessage response = await fixture.CreateClient().SendAsync(
            DeliveryTrackingApi.SendGridCallback(
                fixture,
                DeliveryEventBatch.Of(eventId, $"msg-{Guid.NewGuid():N}", "delivered", seeded)));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);

        await ApplyAsync();
        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Delivered);
        (await CountTrailAsync(seeded.NotificationId)).ShouldBe(
            1, "reprocessar a mesma evidência não pode escrever uma segunda trilha.");
    }

    /// <summary>
    /// Moves whatever the ingestion announced through the relay and the
    /// delivery-tracker consumer, exactly as the two deployed roles would.
    /// Several passes because one pass drains one batch, and the tests share a
    /// queue.
    /// </summary>
    private async Task ApplyAsync(int passes = 3)
    {
        using ServiceProvider relay = fixture.BuildRelayProvider();
        using ServiceProvider tracker = fixture.BuildDeliveryTrackerProvider();
        for (var pass = 0; pass < passes; pass++)
        {
            await DeliveryTrackingFixture.RunRelayPassAsync(relay);
            await DeliveryTrackingFixture.RunTrackerPassAsync(tracker);
        }
    }

    private async Task<int> CountTrailAsync(Guid notificationId)
        => await fixture.QueryAuditDbAsync(async db => await db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == AppliedAction
                && entry.EntityId == notificationId.ToString()));
}
