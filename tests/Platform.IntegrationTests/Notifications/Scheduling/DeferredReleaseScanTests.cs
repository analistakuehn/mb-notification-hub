using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Notifications.Scheduling;

/// <summary>
/// The scan that hands a parked notification back to the pipeline. The
/// transition it writes matters more than the message it enqueues, and the two
/// have to land together: the Core reads any state other than accepted as a
/// redelivery, so a release that only enqueued would leave the notification
/// parked forever while every queue metric showed work being done.
/// </summary>
[Collection(SchedulerScanCollectionDefinition.Name)]
public sealed class DeferredReleaseScanTests(SchedulerScanFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_release_instant_that_passed_returns_the_notification_to_accepted()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Guid notificationId = await fixture.SeedDeferredNotificationAsync(
            NotificationClasses.Transactional, now.AddMinutes(-1));

        (await fixture.RunReleaseScanAsync()).ShouldBeGreaterThanOrEqualTo(1);

        Notification notification = await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .SingleAsync(candidate => candidate.Id == notificationId));
        notification.Status.ShouldBe(
            NotificationStatuses.Accepted,
            "sem a transição dentro da transação do claim o Core trata a retomada como "
            + "duplicata e a notificação adiada nunca sai.");
        notification.ReleaseAt.ShouldNotBeNull();
    }

    [RequiresDockerFact]
    public async Task A_released_notification_is_enqueued_exactly_once_with_a_trail()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Guid notificationId = await fixture.SeedDeferredNotificationAsync(
            NotificationClasses.Transactional, now.AddMinutes(-1));

        await fixture.RunReleaseScanAsync();
        await fixture.RunReleaseScanAsync();

        (await fixture.CountOutboxAsync(notificationId, CoreMessageProcessor.AcceptedMessageType))
            .ShouldBe(1);
        (await fixture.CountReleaseTrailAsync(notificationId)).ShouldBe(1);
    }

    [RequiresDockerFact]
    public async Task A_released_notification_goes_back_to_the_queue_of_its_class()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Guid notificationId = await fixture.SeedDeferredNotificationAsync(
            NotificationClasses.Operational, now.AddMinutes(-1));

        await fixture.RunReleaseScanAsync();

        (await fixture.ReleaseDestinationAsync(notificationId)).ShouldBe(
            $"core-{NotificationClasses.Operational}");
    }

    [RequiresDockerFact]
    public async Task A_notification_still_waiting_is_left_alone()
    {
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        Guid notificationId = await fixture.SeedDeferredNotificationAsync(
            NotificationClasses.Transactional, now.AddHours(4));

        await fixture.RunReleaseScanAsync();

        (await fixture.QueryNotificationsDbAsync(db => db.Notifications
            .AsNoTracking()
            .Where(candidate => candidate.Id == notificationId)
            .Select(candidate => candidate.Status)
            .SingleAsync()))
            .ShouldBe(NotificationStatuses.Deferred);
        (await fixture.CountOutboxAsync(notificationId, CoreMessageProcessor.AcceptedMessageType))
            .ShouldBe(0);
    }

    /// <summary>
    /// Two replicas of the role releasing the same backlog at once. The batch
    /// is far smaller than the backlog on purpose, so the two of them interleave
    /// over many rounds instead of the first one taking everything.
    /// </summary>
    [RequiresDockerFact]
    public async Task Two_replicas_releasing_at_once_release_each_notification_once()
    {
        const int Backlog = 30;
        DateTimeOffset now = fixture.Clock.GetUtcNow();
        List<Guid> parked = [];
        for (var index = 0; index < Backlog; index++)
        {
            parked.Add(await fixture.SeedDeferredNotificationAsync(
                NotificationClasses.Transactional, now.AddMinutes(-1)));
        }

        IDictionary<string, string?> smallBatches = new Dictionary<string, string?>
        {
            [$"{SchedulerScanOptions.SectionName}:BatchSize"] = "3",
        };
        await using ServiceProvider first = fixture.BuildReplicaWith(smallBatches);
        await using ServiceProvider second = fixture.BuildReplicaWith(smallBatches);

        await Task.WhenAll(DrainAsync(first), DrainAsync(second));

        foreach (Guid notificationId in parked)
        {
            (await fixture.CountOutboxAsync(notificationId, CoreMessageProcessor.AcceptedMessageType))
                .ShouldBe(
                    1,
                    "duas réplicas liberando em paralelo enfileiraram a mesma notificação mais "
                    + "de uma vez; a retomada precisa ser reivindicada por linha.");
            (await fixture.CountReleaseTrailAsync(notificationId)).ShouldBe(1);
        }
    }

    private static async Task DrainAsync(ServiceProvider provider)
    {
        for (var round = 0; round < 40; round++)
        {
            using IServiceScope scope = provider.CreateScope();
            DeferredReleaseScanResult result = await scope.ServiceProvider
                .GetRequiredService<DeferredReleaseScan>()
                .RunAsync(CancellationToken.None);
            if (result.Released == 0)
            {
                return;
            }
        }
    }
}
