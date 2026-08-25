using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.DeliveryTracking;

/// <summary>
/// What provider feedback does to the notification behind the attempt. The
/// attempt reaching a confirmed delivery had no reader until now: the state
/// existed and nothing acted on it, so a push that was accepted and never
/// confirmed could not be told apart from one that arrived. These tests close
/// that gap from the outside, through the callback route and the tracker role,
/// and they check the notification and the outgoing announcement, never the
/// applier's internals.
/// </summary>
[Collection(DeliveryTrackingCollectionDefinition.Name)]
public sealed class DeliveryOutcomeTests(DeliveryTrackingFixture fixture)
{
    private const string DeliveredType = "araia.notification.delivered.v1";
    private const string FailedType = "araia.notification.failed.v1";
    private const string FallbackRequestedType = "fallback.requested";

    [RequiresDockerFact]
    public async Task A_confirmed_delivery_ends_the_notification_and_announces_it_exactly_once()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, $"msg-{Guid.NewGuid():N}");
        var recipientId = await RecipientAsync(seeded.NotificationId);

        await CallbackAsync(DeliveryEventBatch.Of(
            $"evt-{Guid.NewGuid():N}", $"msg-{Guid.NewGuid():N}", "delivered", seeded));
        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Delivered);
        (await StatusAsync(seeded.NotificationId)).ShouldBe(
            NotificationStatuses.Delivered,
            "a confirmação do provedor é o que encerra a notificação; sem isso o attempt "
            + "chegaria a 'delivered' e ninguém leria esse estado.");
        (await CountBusEventsAsync(recipientId, DeliveredType)).ShouldBe(1);
        (await CountTrailAsync(seeded.NotificationId, "notification.delivered")).ShouldBe(1);

        // A redelivery of the same evidence must not announce a second result.
        await ApplyAsync();
        (await CountBusEventsAsync(recipientId, DeliveredType)).ShouldBe(1);
        (await CountTrailAsync(seeded.NotificationId, "notification.delivered")).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_bounce_on_a_step_with_a_deadline_asks_the_core_for_the_next_step()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, $"msg-{Guid.NewGuid():N}");
        var recipientId = await RecipientAsync(seeded.NotificationId);
        await StampFallbackDeadlineAsync(seeded.AttemptId, DateTimeOffset.UtcNow.AddSeconds(30));

        await CallbackAsync(DeliveryEventBatch.Bounce(
            $"evt-{Guid.NewGuid():N}", $"msg-{Guid.NewGuid():N}", seeded, "bounce"));
        await ApplyAsync();

        (await DeliveryTrackingApi.ReadAttemptStatusAsync(fixture, seeded.AttemptId))
            .ShouldBe(NotificationAttemptStatuses.Bounced);
        (await StatusAsync(seeded.NotificationId)).ShouldBe(
            NotificationStatuses.Dispatched,
            "a etapa tem passo posterior, então a notificação continua aberta.");
        (await CountBusEventsAsync(recipientId, FallbackRequestedType)).ShouldBe(
            1,
            "uma recusa do destino esgota a etapa e pede o próximo passo, exatamente como "
            + "a recusa síncrona do provedor já pede.");
        (await CountTrailAsync(seeded.NotificationId, "fallback.triggered")).ShouldBe(1);
        (await CountBusEventsAsync(recipientId, DeliveredType)).ShouldBe(0);
    }

    [RequiresDockerFact]
    public async Task A_bounce_on_the_last_step_ends_the_notification_on_failed()
    {
        SeededAttempt seeded = await DeliveryTrackingApi.SeedAttemptAsync(
            fixture, "email", DeliveryTrackingApi.SendGridProvider, $"msg-{Guid.NewGuid():N}");
        var recipientId = await RecipientAsync(seeded.NotificationId);

        await CallbackAsync(DeliveryEventBatch.Bounce(
            $"evt-{Guid.NewGuid():N}", $"msg-{Guid.NewGuid():N}", seeded, "bounce"));
        await ApplyAsync();

        (await StatusAsync(seeded.NotificationId)).ShouldBe(NotificationStatuses.Failed);
        (await CountBusEventsAsync(recipientId, FailedType)).ShouldBe(1);
        (await CountBusEventsAsync(recipientId, FallbackRequestedType)).ShouldBe(0);
        (await CountTrailAsync(seeded.NotificationId, "notification.failed")).ShouldBe(1);
    }

    private async Task CallbackAsync(string body)
    {
        HttpResponseMessage response = await fixture.CreateClient()
            .SendAsync(DeliveryTrackingApi.SendGridCallback(fixture, body));
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
    }

    /// <summary>
    /// Moves whatever the callback announced through the relay and the
    /// delivery-tracker consumer, exactly as the two deployed roles would.
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

    /// <summary>
    /// Gives the seeded attempt a later plan step. The seeding helper writes
    /// the last step of a plan, and a deadline is exactly what tells the two
    /// cases apart.
    /// </summary>
    private async Task StampFallbackDeadlineAsync(Guid attemptId, DateTimeOffset deadline)
        => await fixture.ExecuteNotificationsDbAsync(db => db.Database.ExecuteSqlAsync(
            $"""
            UPDATE notifications.notification_attempt
            SET fallback_deadline = {deadline}
            WHERE id = {attemptId}
            """));

    private async Task<string> RecipientAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.RecipientId)
            .SingleAsync());

    private async Task<string> StatusAsync(Guid notificationId)
        => await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(notification => notification.Id == notificationId)
            .Select(notification => notification.Status)
            .SingleAsync());

    private async Task<int> CountBusEventsAsync(string recipientId, string eventType)
        => await fixture.QueryPlatformDbAsync(db => db.OutboxMessages
            .AsNoTracking()
            .CountAsync(message => message.EventType == eventType
                && message.MessageKey == recipientId));

    private async Task<int> CountTrailAsync(Guid notificationId, string action)
        => await fixture.QueryAuditDbAsync(db => db.AuditEvents
            .AsNoTracking()
            .CountAsync(entry => entry.Action == action
                && entry.EntityId == notificationId.ToString()));
}
