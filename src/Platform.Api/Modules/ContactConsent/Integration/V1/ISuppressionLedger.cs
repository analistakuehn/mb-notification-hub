using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>How one reported delivery signal settled against the ledger.</summary>
public enum SuppressionOutcome
{
    /// <summary>
    /// The signal is recorded and the channel's accumulation rule has not been
    /// met yet, so the contact point stays addressable.
    /// </summary>
    SignalRecorded = 0,

    /// <summary>
    /// The signal completed the channel's accumulation rule: the contact point
    /// is suppressed, the trail names the actor and the outgoing event
    /// announces it once.
    /// </summary>
    ContactSuppressed = 1,

    /// <summary>
    /// This exact source event was already reported. Nothing was written a
    /// second time; the repeated report leaves its own trail and no effect.
    /// </summary>
    AlreadyApplied = 2,
}

/// <summary>
/// One piece of delivery feedback about a destination, as the reporting module
/// observed it. It carries no provider payload and no contact value: which
/// address the contact point holds stays inside this module, and the reporter
/// only names the row it already addressed.
/// </summary>
/// <param name="RecipientId">Owner of the contact point; a foreign point fails as not found.</param>
/// <param name="ContactPointId">The destination the provider refused.</param>
/// <param name="Channel">Channel of the contact point, as the reporter routed it.</param>
/// <param name="Reason">Stable classification of the signal, from the provider-knowledge side.</param>
/// <param name="SourceEventId">
/// Identity of the stored evidence row that originated the report. It carries a
/// unique key on this side, so a redelivery of the internal message is a
/// declarative no-op with a trail of its own instead of a second effect.
/// </param>
/// <param name="ObservedAt">
/// When the reporting hub observed the signal. Never the instant the provider
/// claims: an accumulation window measured on a provider-controlled instant
/// could be slid open from outside.
/// </param>
public sealed record SuppressionReport(
    string RecipientId,
    Guid ContactPointId,
    string Channel,
    string Reason,
    Guid SourceEventId,
    DateTimeOffset ObservedAt);

/// <summary>
/// In-process write surface of the suppression ledger this module owns, for
/// the delivery-feedback path: when a provider refuses a destination
/// definitively, the delivery tracker reports the signal here and this module
/// decides, records and announces the consequence, exactly like every other
/// write of this context.
/// <para>
/// The accumulation rule stays behind this contract on purpose. Deciding
/// whether one refusal is enough needs the history of signals for the contact
/// point, and that history is contact data: exporting it to let a caller
/// decide would export the very thing this module exists to hold.
/// </para>
/// </summary>
public interface ISuppressionLedger
{
    /// <summary>
    /// Records one delivery signal and applies the channel's accumulation
    /// rule. An unknown or foreign contact point fails as not found; a channel
    /// that disagrees with the contact point fails as a business rule
    /// violation, because a rule of one channel must never settle another.
    /// </summary>
    Task<Result<SuppressionOutcome>> ReportDeliveryFeedbackAsync(
        SuppressionReport report,
        CancellationToken cancellationToken);
}
