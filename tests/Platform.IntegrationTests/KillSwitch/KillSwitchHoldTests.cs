using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.KillSwitch;

[Collection(CorePipelineCollectionDefinition.Name)]
public sealed class KillSwitchHoldTests(CorePipelineFixture fixture)
{
    [RequiresDockerFact]
    public async Task A_released_hold_reopens_once_and_replayed_retention_is_idempotent()
    {
        var notificationId = Guid.CreateVersion7();
        var request = new KillSwitchHoldRequest
        {
            WorkKind = KillSwitchWorkKinds.Core,
            WorkId = $"core:{notificationId:N}",
            Scope = KillSwitchScope.Application,
            Key = $"application-{Guid.NewGuid():N}",
            Destination = "core-transactional",
            PayloadJson = $$"""{"notificationId":"{{notificationId}}"}""",
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        };

        await using ServiceProvider worker = fixture.BuildCoreWorkerProvider();
        await using AsyncServiceScope scope = worker.CreateAsyncScope();
        NotificationsDbContext db = scope.ServiceProvider.GetRequiredService<NotificationsDbContext>();
        KillSwitchHoldWriter writer = new(db);
        await writer.HoldAsync(request, claimedAttemptId: null, CancellationToken.None);
        DateTimeOffset releasedAt = DateTimeOffset.UtcNow;
        await db.KillSwitchHolds
            .Where(hold => hold.WorkKind == request.WorkKind && hold.WorkId == request.WorkId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(hold => hold.ReleasedAt, releasedAt)
                .SetProperty(hold => hold.Version, hold => hold.Version + 1));

        await writer.HoldAsync(request, claimedAttemptId: null, CancellationToken.None);
        await writer.HoldAsync(request, claimedAttemptId: null, CancellationToken.None);

        db.ChangeTracker.Clear();
        KillSwitchHold stored = await db.KillSwitchHolds
            .AsNoTracking()
            .SingleAsync(hold => hold.WorkKind == request.WorkKind && hold.WorkId == request.WorkId);
        stored.ReleasedAt.ShouldBeNull();
        stored.Version.ShouldBe(3);
    }
}
