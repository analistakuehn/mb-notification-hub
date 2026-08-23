using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Redis;

/// <summary>
/// Connection of the recipient snapshot cache. The cache is an availability
/// layer over the local store: an unreachable Redis never blocks a read, so
/// the connection is tuned to fail fast and reconnect in the background.
/// </summary>
public sealed class ContactConsentRedisOptions
{
    public const string SectionName = "Modules:ContactConsent:Redis";

    [Required]
    public required string ConnectionString { get; init; }

    /// <summary>Prefix of every key this module writes.</summary>
    public string KeyPrefix { get; init; } = "contact-consent:";

    /// <summary>
    /// Lifetime of one cached snapshot and the ceiling of any last-known
    /// read: past it the entry is gone, whatever the degradation.
    /// </summary>
    public TimeSpan Ttl { get; init; } = TimeSpan.FromHours(24);
}
