using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal sealed class KillSwitchHoldReleaser(
    NotificationsDbContext db,
    IOutboxWriter outboxWriter,
    TimeProvider timeProvider,
    ILogger<KillSwitchHoldReleaser> logger)
{
    private const int ReleaseBatchSize = 100;
    private const int QuarantineAllowance = 100;
    private const string InvalidClaimReason = "invalid-claim-payload";
    private const string MissingNotificationReason = "notification-not-found";

    internal async Task<int> ReleaseBatchAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<Guid> candidates = await db.KillSwitchHolds
            .AsNoTracking()
            .Where(hold => hold.ReleasedAt == null
                && (hold.ExpiresAt <= now
                    || !db.KillSwitches.Any(entry => entry.Scope == hold.Scope
                        && entry.Key == hold.Key
                        && entry.State == KillSwitchStates.Active)))
            .OrderBy(hold => hold.ExpiresAt)
            .ThenBy(hold => hold.Id)
            .Select(hold => hold.Id)
            .Take(ReleaseBatchSize + QuarantineAllowance)
            .ToListAsync(cancellationToken);
        var released = 0;
        foreach (Guid holdId in candidates)
        {
            if (released == ReleaseBatchSize)
            {
                break;
            }

            if (await TryReleaseAsync(holdId, now, cancellationToken))
            {
                released++;
            }
        }

        return released;
    }

    private async Task<bool> TryReleaseAsync(
        Guid holdId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        KillSwitchHold? hold = await db.KillSwitchHolds
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == holdId, cancellationToken);
        if (hold is null || hold.ReleasedAt is not null)
        {
            return false;
        }

        var expired = hold.ExpiresAt <= now;
        if (!expired)
        {
            var remainsActive = await db.KillSwitches
                .AsNoTracking()
                .AnyAsync(entry => entry.Scope == hold.Scope
                    && entry.Key == hold.Key
                    && entry.State == KillSwitchStates.Active,
                    cancellationToken);
            if (remainsActive)
            {
                return false;
            }
        }

        if (!TryReadNotificationId(hold.PayloadJson, out Guid notificationId))
        {
            return await TerminalizeWithoutResumeAsync(
                hold.Id,
                now,
                InvalidClaimReason,
                cancellationToken);
        }

        Notification? notification = await NotificationForAsync(notificationId, cancellationToken);
        if (notification is null)
        {
            return await TerminalizeWithoutResumeAsync(
                hold.Id,
                now,
                MissingNotificationReason,
                cancellationToken);
        }

        OutboxAppend resumeMessage;
        try
        {
            resumeMessage = KillSwitchResumeMessages.Build(hold, notification, now);
        }
        catch (JsonException)
        {
            return await TerminalizeWithoutResumeAsync(
                hold.Id,
                now,
                InvalidClaimReason,
                cancellationToken);
        }
        catch (InvalidOperationException)
        {
            return await TerminalizeWithoutResumeAsync(
                hold.Id,
                now,
                InvalidClaimReason,
                cancellationToken);
        }

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var claimed = await db.KillSwitchHolds
            .Where(candidate => candidate.Id == hold.Id && candidate.ReleasedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(candidate => candidate.ReleasedAt, now)
                    .SetProperty(candidate => candidate.Version, candidate => candidate.Version + 1),
                cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await outboxWriter.AppendAsync(
            transaction.GetDbTransaction(),
            resumeMessage,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    internal static bool TryReadNotificationId(string payloadJson, out Guid notificationId)
    {
        notificationId = default;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            JsonElement payload = document.RootElement;
            return payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("notificationId", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && Guid.TryParse(value.GetString(), out notificationId);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<Notification?> NotificationForAsync(
        Guid notificationId,
        CancellationToken cancellationToken)
        => await db.Notifications
            .AsNoTracking()
            .SingleOrDefaultAsync(notification => notification.Id == notificationId, cancellationToken);

    private async Task<bool> TerminalizeWithoutResumeAsync(
        Guid holdId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        var terminalized = await db.KillSwitchHolds
            .Where(hold => hold.Id == holdId && hold.ReleasedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(hold => hold.ReleasedAt, now)
                    .SetProperty(hold => hold.Version, hold => hold.Version + 1),
                cancellationToken);
        if (terminalized == 1)
        {
            logger.KillSwitchHoldTerminalized(holdId, reason);
        }

        return false;
    }
}

internal static partial class KillSwitchHoldReleaserLogger
{
    [LoggerMessage(
        EventId = 7162,
        Level = LogLevel.Warning,
        Message = "Hold {HoldId} do kill switch terminalizado sem retomada ({Reason}).")]
    internal static partial void KillSwitchHoldTerminalized(
        this ILogger logger,
        Guid holdId,
        string reason);
}
