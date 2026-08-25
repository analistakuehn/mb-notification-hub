using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Configuration of the provider deduplication purge. The retention is what
/// bounds the ledger: a provider redelivers a callback for as long as its own
/// retry policy lasts, and a mark only becomes removable once no redelivery of
/// the event it names can still arrive.
/// </summary>
public sealed class ProviderEventDedupePurgeOptions
{
    public const string SectionName = "Modules:Notifications:ProviderEventDedupePurge";

    public bool Enabled { get; init; } = true;

    /// <summary>Pause between purge rounds; the first round runs at host start.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Age past which a deduplication mark is removed.
    /// <para>
    /// It is also the freshness guarantee of one channel, which is why the
    /// default is not merely long. One provider signs a timestamp with its
    /// payload and this hub refuses a callback outside a narrow window around
    /// it; the other signs the URL and the form and no instant at all, so a
    /// captured callback of that channel stays cryptographically valid for
    /// ever. Against that provider the only thing standing between a captured
    /// callback and a second acceptance is this mark, so its lifetime is the
    /// horizon of the risk and not a storage decision.
    /// </para>
    /// <para>
    /// The default matches the window the delivery applier resolves an attempt
    /// inside, and the two are asserted against each other rather than kept in
    /// step by hand. That is what makes the guarantee complete: past the mark a
    /// replay would be accepted at the door again, and past the window it finds
    /// no attempt to describe, so a gap between the two is the only interval in
    /// which a replay could write evidence a second time.
    /// </para>
    /// </summary>
    [Range(typeof(TimeSpan), "1.00:00:00", "365.00:00:00")]
    public TimeSpan Retention { get; init; } = NotificationPlanOutcome.AttemptWindow;
}
