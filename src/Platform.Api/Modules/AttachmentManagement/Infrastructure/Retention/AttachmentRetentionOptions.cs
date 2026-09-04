using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;

/// <summary>
/// Configuration of the sweep that removes the content of abandoned
/// attachments: how often it runs, how much one round takes on, and how long
/// each state that can be abandoned is kept.
/// <para>
/// The first two bound the job. The four windows decide what the job does, and
/// none of them has a default that closes, for the same reason the capacity
/// section next to it has none: a window nobody configured cannot mean zero,
/// because zero removes the content of every attachment the moment it reaches
/// the state, which is a decision about the product taken by an omission. Zero
/// is therefore the mark of a value nobody set, the startup guard refuses it,
/// and the process does not come up without the section.
/// </para>
/// </summary>
public sealed class AttachmentRetentionOptions
{
    public const string SectionName = "Modules:AttachmentManagement:Retention";

    /// <summary>
    /// The shortest any window may be, and the one value here that is derived
    /// rather than chosen.
    /// <para>
    /// An attempt whose outcome nobody reported keeps a dependency over the
    /// attachment until the delivery side resolves it, and that side goes
    /// looking once its own staleness cut has passed and then once per round
    /// of a job that runs daily. Six hours plus a day is the longest an
    /// attempt can sit unresolved, so a window shorter than that would let
    /// this sweep reach an attachment while the system itself still had
    /// something to say about it.
    /// </para>
    /// <para>
    /// It is a number and not a reading of that job's configuration on
    /// purpose: this module does not read another context's settings. What
    /// keeps it honest is a test that holds it against the values that job
    /// actually runs with, which fails the day they move past it.
    /// </para>
    /// </summary>
    public static readonly TimeSpan UnresolvedAttemptHorizon = TimeSpan.FromHours(30);

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Pause between rounds. Hours rather than minutes, because what a round
    /// removes has been abandoned for days: nobody is waiting for it, and the
    /// only thing a shorter pause buys is more listings of keys that have
    /// nothing under them.
    /// </summary>
    [Range(typeof(TimeSpan), "00:01:00", "1.00:00:00")]
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How many abandoned attachments one round takes on. It bounds the round
    /// and not the backlog: what a round leaves behind is picked up by the
    /// next one, oldest first, and each one costs a lock, a listing and at
    /// least one call to the store.
    /// </summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 100;

    /// <summary>How long an attachment whose bytes never arrived is kept.</summary>
    public TimeSpan UnstartedUpload { get; init; }

    /// <summary>How long content nobody asked a verdict about is kept.</summary>
    public TimeSpan UnvalidatedContent { get; init; }

    /// <summary>How long the content of a refused attachment is kept.</summary>
    public TimeSpan RefusedContent { get; init; }

    /// <summary>
    /// How long the content of an attachment whose release was taken back is
    /// kept.
    /// </summary>
    public TimeSpan WithdrawnRelease { get; init; }

    /// <summary>
    /// The four windows as the rule reads them. The aggregate decides what is
    /// abandoned and it is handed the values rather than holding them, so this
    /// is the only place the configured section and the rule meet.
    /// </summary>
    public AttachmentRetentionWindows Windows()
        => new(UnstartedUpload, UnvalidatedContent, RefusedContent, WithdrawnRelease);
}
