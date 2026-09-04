using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;

/// <summary>
/// Configuration of the repair round. Every knob decides how often the
/// outstanding repairs are carried out and how many of them one round takes
/// on, and none of them decides what a repair is or when one is owed: that is
/// written on the row by whoever discovered it.
/// </summary>
public sealed class AttachmentReconciliationOptions
{
    public const string SectionName = "Modules:AttachmentManagement:Reconciliation";

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Pause between rounds. Minutes rather than hours, because what a round
    /// repairs is a key nobody can upload to: while it waits, the producer
    /// that owns that attachment is being refused, and the refusal is the same
    /// one it would get from its own mistake.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:15", "1.00:00:00")]
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many outstanding repairs one round takes on. It bounds the round
    /// and not the backlog: what a round leaves behind is picked up by the
    /// next one, oldest first, and each repair costs at least one call to the
    /// store.
    /// </summary>
    [Range(1, 5000)]
    public int BatchSize { get; init; } = 100;
}
