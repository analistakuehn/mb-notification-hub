using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

/// <summary>
/// The send rate one provider contracted with this deployment, plus how much
/// of it may be spent at once.
/// </summary>
public sealed record ProviderRateLimit
{
    /// <summary>Sustained sends per second the provider accepts from this deployment.</summary>
    [Range(1, 100_000)]
    public required int PermitsPerSecond { get; init; }

    /// <summary>
    /// Seconds of budget the bucket holds, which is what a burst may spend
    /// back to back before the refill rate takes over. One second is the
    /// shipped value: it absorbs a batch that arrives together without ever
    /// handing the provider more than a second of traffic at once.
    /// </summary>
    [Range(1, 60)]
    public int BurstSeconds { get; init; } = 1;

    /// <summary>Tokens the bucket holds when full.</summary>
    internal double Capacity => (double)PermitsPerSecond * BurstSeconds;

    /// <summary>
    /// How long an untouched bucket survives in the store. A bucket idle for
    /// longer than its own refill span is indistinguishable from a full one,
    /// so letting the key expire loses nothing and keeps the store free of
    /// providers that stopped sending.
    /// </summary>
    internal TimeSpan KeyTtl => TimeSpan.FromSeconds(BurstSeconds + 1);
}

/// <summary>
/// Per-provider send rate and the Redis that holds the buckets. A provider
/// without an entry has no limit, in the same regime the ingestion limits
/// follow: the values are an operational decision and a contracted number
/// changes without a deploy. The store lives in this section because the rate
/// limit is the only control of this module backed by it; a second one would
/// earn a connection section of its own.
/// </summary>
public sealed class ProviderRateLimitOptions
{
    public const string SectionName = "Modules:Dispatch:RateLimits";

    private readonly Lazy<IReadOnlyDictionary<string, ProviderRateLimit>> _byProvider;

    public ProviderRateLimitOptions()
        => _byProvider = new Lazy<IReadOnlyDictionary<string, ProviderRateLimit>>(
            () => new Dictionary<string, ProviderRateLimit>(PerProvider, StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Redis holding the buckets. Empty turns the control off, which is what a
    /// local environment and a deployment without contracted limits have.
    /// </summary>
    public string RedisConnectionString { get; init; } = "";

    /// <summary>Prefix of every bucket key this module writes.</summary>
    public string KeyPrefix { get; init; } = "dispatch:";

    /// <summary>One rate per provider key, keyed as the adapter names itself.</summary>
    public Dictionary<string, ProviderRateLimit> PerProvider { get; init; } = [];

    /// <summary>The rate contracted for one provider, or null when it has none.</summary>
    internal ProviderRateLimit? For(string providerKey)
        => _byProvider.Value.GetValueOrDefault(providerKey);
}
