using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Features.Dispatching;

/// <summary>
/// What the revalidation said about the set one attempt is about to carry.
/// Only one of the four lets the send happen.
/// </summary>
internal enum AttachmentPreflightOutcome
{
    /// <summary>
    /// Nothing is known about the set: the check did not conclude, or the
    /// stored document stopped naming a set anybody can read. It is not a
    /// statement that the set is unusable, it is the absence of a statement.
    /// <para>
    /// It is the value zero on purpose, and the one refusal at zero rather
    /// than a definite one, because the two refusals settle an attempt very
    /// differently. A verdict nobody produced holds the attempt and asks
    /// again; had the definite refusal been the default, the same silence
    /// would have ended the notification for good.
    /// </para>
    /// </summary>
    Undecided = 0,

    /// <summary>
    /// At least one member no longer carries a release in force over the
    /// content it was accepted with. The set may not be used, and no later
    /// reading of it is going to say otherwise on its own.
    /// </summary>
    Withheld,

    /// <summary>
    /// The set no longer fits what one notification may carry: too many
    /// members, or members adding up to more than the envelope allows.
    /// </summary>
    OverCapacity,

    /// <summary>
    /// The notification carries no set at all, or it carries one that fits and
    /// whose every member is still released, still within its validity and
    /// still the content it was accepted with. It says so as of this instant
    /// and it is not a permission that survives being carried around.
    /// </summary>
    Clear,
}

/// <summary>
/// What the revalidation concluded and, when it cleared the send, the very set
/// it concluded it over.
/// <para>
/// The two travel together because the caller must submit the set that was
/// verified and not a second reading of the document. Reading the row again
/// between the verdict and the call would open a window for the row to change,
/// and the send would then carry a composition nothing checked.
/// </para>
/// <para>
/// The set is null whenever the send may not happen, and also on the ordinary
/// path of a notification that named no attachments: there is no set to
/// submit in either case, and a caller that reads it as absence of attachments
/// is right both times.
/// </para>
/// </summary>
internal readonly record struct AttachmentPreflightResult(
    AttachmentPreflightOutcome Outcome,
    AcceptedAttachmentSet? Accepted)
{
    internal static AttachmentPreflightResult Refused(AttachmentPreflightOutcome outcome)
        => new(outcome, null);

    /// <summary>The send may happen and carries nothing.</summary>
    internal static AttachmentPreflightResult ClearWithoutAttachments()
        => new(AttachmentPreflightOutcome.Clear, null);

    /// <summary>The send may happen and carries exactly this set.</summary>
    internal static AttachmentPreflightResult Clear(AcceptedAttachmentSet accepted)
        => new(AttachmentPreflightOutcome.Clear, accepted);
}

/// <summary>
/// Revalidates the accepted set in the window between the claim of an attempt
/// and the call that cannot be taken back.
/// <para>
/// The snapshot on the notification row froze identity and composition and
/// froze no eligibility at all, so release, revocation, validity and the
/// identity of the content are read again here, every time. A send that
/// skipped this would be delivering on a release taken back an hour after the
/// notification was accepted, and on a set that grew past the envelope the day
/// an operator tightened it.
/// </para>
/// <para>
/// The capacity is measured before the release is read, because it costs no
/// store access: a set that could not go out whatever its releases say never
/// reaches the record at all. Both answers come from the owning module, and
/// what crosses this boundary is a verdict rather than a limit or a digest, so
/// nothing here compares anything it would have to be told the rules for.
/// </para>
/// </summary>
internal sealed class AttachmentPreflight(
    IAttachmentEnvelopeCheck envelopeCheck,
    IAttachmentReleaseCheck releaseCheck,
    ILogger<AttachmentPreflight> logger)
{
    internal async Task<AttachmentPreflightResult> VerifyAsync(
        Notification notification,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);

        AcceptedManifestRead stored = AcceptedAttachmentManifest.Read(
            notification.AcceptedAttachmentsJson);
        if (stored is AcceptedManifestRead.Absent)
        {
            return AttachmentPreflightResult.ClearWithoutAttachments();
        }

        if (stored is not AcceptedManifestRead.Present present)
        {
            // The document read whole a moment ago, before the attempt was
            // claimed, so meeting one that does not read here means the row
            // changed underneath this send. Nothing is settled: the attempt
            // goes back, and the gate that runs before the claim is the one
            // that reports the defect on the redelivery, loudly and with the
            // attempt still claimable.
            logger.PreflightMetAnUnreadableSet(notification.Id);
            return AttachmentPreflightResult.Refused(AttachmentPreflightOutcome.Undecided);
        }

        if (envelopeCheck.Measure(present.Accepted) != AttachmentEnvelopeVerdict.WithinEnvelope)
        {
            return AttachmentPreflightResult.Refused(AttachmentPreflightOutcome.OverCapacity);
        }

        AttachmentReleaseVerdict verdict = await releaseCheck.VerifyAsync(
            present.Accepted, cancellationToken);
        return verdict switch
        {
            // The set that clears is the set that was measured and asked
            // about, handed back so the caller submits this object and never
            // a second reading of the row.
            AttachmentReleaseVerdict.Deliverable => AttachmentPreflightResult.Clear(present.Accepted),
            AttachmentReleaseVerdict.Withheld =>
                AttachmentPreflightResult.Refused(AttachmentPreflightOutcome.Withheld),

            // Every other answer, including one this code does not know yet,
            // is the absence of a statement rather than a statement. Throwing
            // on it would strand an attempt this worker has already claimed.
            _ => AttachmentPreflightResult.Refused(AttachmentPreflightOutcome.Undecided),
        };
    }
}
