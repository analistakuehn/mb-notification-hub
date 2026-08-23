using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>
/// Configuration of the one-shot backfill over rendered content written before
/// the two-form seal existed. Off by default and run under an explicit gate,
/// because it rewrites stored ciphertext: an operator turns it on, watches the
/// round and the review list, and turns it off again.
/// </summary>
public sealed class RenderedContentBackfillOptions
{
    public const string SectionName = "Modules:Notifications:RenderedContentBackfill";

    public bool Enabled { get; init; }

    /// <summary>How many attempts one pass settles; a larger set needs more passes.</summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 500;

    /// <summary>
    /// Wait after the notification expiry before an attempt that never reached
    /// a terminal verdict enters the pass. An attempt still inside the window
    /// may yet be sent, and the content it was queued with is the content it
    /// must send.
    /// </summary>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan Grace { get; init; } = TimeSpan.FromHours(1);
}
