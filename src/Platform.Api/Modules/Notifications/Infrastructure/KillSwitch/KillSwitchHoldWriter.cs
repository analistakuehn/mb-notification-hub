using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal sealed record KillSwitchHoldRequest
{
    internal required string WorkKind { get; init; }

    internal required string WorkId { get; init; }

    internal required KillSwitchScope Scope { get; init; }

    internal required string Key { get; init; }

    internal required string Destination { get; init; }

    internal required string PayloadJson { get; init; }

    internal required DateTimeOffset ExpiresAt { get; init; }
}

internal sealed class KillSwitchHoldWriter(NotificationsDbContext db)
{
    internal async Task HoldAsync(
        KillSwitchHoldRequest request,
        Guid? claimedAttemptId,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        if (claimedAttemptId is { } attemptId)
        {
            var reverted = await db.NotificationAttempts
                .Where(attempt => attempt.Id == attemptId
                    && attempt.Status == NotificationAttemptStatuses.Sending)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(attempt => attempt.Status, NotificationAttemptStatuses.Queued)
                        .SetProperty(attempt => attempt.ProviderKey, (string?)null),
                    cancellationToken);
            if (reverted != 1)
            {
                throw new InvalidOperationException(
                    $"O attempt {attemptId} não estava em 'sending' ao aplicar o hold de canal.");
            }
        }

        var holdId = Guid.CreateVersion7();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO notifications.kill_switch_hold
                (id, work_kind, work_id, scope, key, destination, payload,
                 expires_at, released_at, version)
            VALUES
                ({holdId}, {request.WorkKind}, {request.WorkId}, {request.Scope.Canonical()},
                 {request.Key}, {request.Destination}, CAST({request.PayloadJson} AS jsonb),
                 {request.ExpiresAt}, NULL, 1)
            ON CONFLICT (work_kind, work_id) DO UPDATE
            SET scope = EXCLUDED.scope,
                key = EXCLUDED.key,
                destination = EXCLUDED.destination,
                payload = EXCLUDED.payload,
                expires_at = EXCLUDED.expires_at,
                released_at = NULL,
                version = kill_switch_hold.version + 1
            WHERE kill_switch_hold.released_at IS NOT NULL
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static KillSwitchHoldRequest Core(
        Notification notification,
        MessageEnvelope envelope,
        string workKind,
        KillSwitchScope scope,
        string key)
    {
        var destination = RequiredDestination(envelope);
        JsonElement payload = envelope.Payload;
        var workId = workKind == KillSwitchWorkKinds.Fallback
            ? $"fallback:{RequiredGuid(payload, "failedAttemptId"):N}"
            : $"core:{notification.Id:N}";
        object claimCheck = workKind == KillSwitchWorkKinds.Fallback
            ? new
            {
                notificationId = notification.Id,
                failedAttemptId = RequiredGuid(payload, "failedAttemptId"),
            }
            : new { notificationId = notification.Id };
        return new KillSwitchHoldRequest
        {
            WorkKind = workKind,
            WorkId = workId,
            Scope = scope,
            Key = key,
            Destination = destination,
            PayloadJson = JsonSerializer.Serialize(claimCheck),
            ExpiresAt = notification.ExpiresAt,
        };
    }

    internal static KillSwitchHoldRequest Dispatch(
        Notification notification,
        NotificationAttempt attempt,
        MessageEnvelope envelope)
        => new()
        {
            WorkKind = KillSwitchWorkKinds.Dispatch,
            WorkId = $"dispatch:{attempt.Id:N}",
            Scope = KillSwitchScope.Channel,
            Key = attempt.Channel,
            Destination = RequiredDestination(envelope),
            PayloadJson = JsonSerializer.Serialize(new
            {
                notificationId = notification.Id,
                attemptId = attempt.Id,
            }),
            ExpiresAt = notification.ExpiresAt,
        };

    private static string RequiredDestination(MessageEnvelope envelope)
        => envelope.SourceQueue
            ?? throw new InvalidOperationException(
                "O hold requer a fila de origem para retomar o trabalho sem reconstruir roteamento.");

    private static Guid RequiredGuid(JsonElement payload, string name)
        => payload.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out Guid parsed)
                ? parsed
                : throw new InvalidOperationException($"O claim check não contém '{name}' válido.");
}
