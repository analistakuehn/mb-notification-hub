using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

/// <summary>
/// Volume alarm of the content route. The rate limit caps how fast one
/// principal may open stored content; this alarm is the other half, and it fires
/// well below the cap, because the risk is not a burst, it is a patient sweep
/// that never trips a per-minute ceiling.
/// </summary>
public sealed class ContentDisclosureAlarmOptions
{
    public const string SectionName = "Modules:Compliance:ContentDisclosureAlarm";

    /// <summary>Openings by one principal inside the window that raise the alarm.</summary>
    [Range(1, 10_000)]
    public int Threshold { get; init; } = 20;

    [Range(1, 1_440)]
    public int WindowMinutes { get; init; } = 10;
}
