using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

/// <summary>
/// Configuration of the WORM export of the audit trail: where the evidence
/// goes, for how long it stays immutable, and which of the two triggers is
/// active. Both triggers ship enabled because writing evidence is additive and
/// reversible by nature; the destructive end of the cycle has its own gates in
/// the partition-manager section, and no single switch ever turns on export
/// and destruction together.
/// </summary>
public sealed class WormExportOptions
{
    public const string SectionName = "Modules:Audit:WormExport";

    /// <summary>Bucket with Object Lock enabled; provisioned by infrastructure, never by this job.</summary>
    [Required]
    public required string Bucket { get; init; }

    /// <summary>Key prefix of every exported object, versioned so a format change never overwrites evidence.</summary>
    [Required]
    public string KeyPrefix { get; init; } = "audit-export/v1";

    /// <summary>
    /// Immutable retention applied to every written object, in years. The
    /// legal term is a business decision; this default is the conservative
    /// floor the design assumes until it is confirmed.
    /// </summary>
    [Range(1, 30)]
    public int RetentionYears { get; init; } = 5;

    /// <summary>Daily slice export of the still-open partitions.</summary>
    public bool EnableDailyExport { get; init; } = true;

    /// <summary>Authoritative whole-partition export executed by the closing cycle.</summary>
    public bool EnableClosingExport { get; init; } = true;

    /// <summary>
    /// How long after the end of a day the slice becomes exportable. It gives
    /// in-flight transactions time to land, so a day is exported once and
    /// stays complete; it must stay above the verification watermark, because
    /// evidence is only exported after the chain over it has been checked.
    /// </summary>
    public TimeSpan StabilizationDelay { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>Custom endpoint of the object store; null uses the AWS default.</summary>
    public string? ServiceUrl { get; init; }

    public string? Region { get; init; }

    public string? AccessKey { get; init; }

    public string? SecretKey { get; init; }

    /// <summary>
    /// Path-style addressing, required by the local emulator, which does not
    /// resolve virtual-hosted bucket names.
    /// </summary>
    public bool ForcePathStyle { get; init; }
}
