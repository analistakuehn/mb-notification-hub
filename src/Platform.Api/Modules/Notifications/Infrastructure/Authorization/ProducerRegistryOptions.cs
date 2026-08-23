using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

/// <summary>
/// Tuning of the producer-registry reader. Only the cache window is
/// configurable: the grants themselves have exactly one source, the
/// materialized table, because a second source of authorization outside the
/// auditable trail is how a grant nobody reviewed reaches production.
/// </summary>
public sealed class ProducerRegistryOptions
{
    public const string SectionName = "Modules:Notifications:ProducerRegistry";

    /// <summary>How long a snapshot serves before a refresh; sixty seconds mirrors the provider configuration.</summary>
    [Range(1, 3600)]
    public int CacheTtlSeconds { get; init; } = 60;
}
