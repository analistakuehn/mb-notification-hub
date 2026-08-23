using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;

/// <summary>
/// Connection of the ingestion control plane (rate limit and idempotency fast
/// path). Every control backed by it fails open: an unreachable Redis never
/// blocks an ingestion request, so the connection is tuned to fail fast and
/// reconnect in the background instead of aborting.
/// </summary>
public sealed class NotificationsRedisOptions
{
    public const string SectionName = "Modules:Notifications:Redis";

    [Required]
    public required string ConnectionString { get; init; }

    /// <summary>Prefix of every key this module writes.</summary>
    public string KeyPrefix { get; init; } = "notifications:";
}
