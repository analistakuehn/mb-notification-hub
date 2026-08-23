using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

/// <summary>One fixed counting window of the ingestion rate limit.</summary>
public sealed record RateWindow
{
    [Range(1, int.MaxValue)]
    public required int PermitLimit { get; init; }

    [Range(1, 604_800)]
    public required int WindowSeconds { get; init; }
}

/// <summary>
/// Ingestion rate limits in two dimensions, both keyed by canonical class:
/// per producer principal (protects the platform from a compromised or
/// runaway producer) and per recipient (protects one person from message
/// bombing). A class without a configured entry has no limit in that
/// dimension. The values live in configuration because tuning them is an
/// operational decision, not a deploy.
/// </summary>
public sealed class IngestionRateLimitOptions
{
    public const string SectionName = "Modules:Notifications:RateLimits";

    /// <summary>One window per class, keyed by canonical class value.</summary>
    public Dictionary<string, RateWindow> PerPrincipal { get; init; } = [];

    /// <summary>
    /// Cumulative windows per class, keyed by canonical class value; every
    /// window must hold for the request to pass.
    /// </summary>
    public Dictionary<string, List<RateWindow>> PerRecipient { get; init; } = [];
}
