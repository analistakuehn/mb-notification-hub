using System.Data.Common;

namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// Why a claim did not accept the set. One word covers every way a set may not
/// be had, because the difference between an attachment that does not exist and
/// one that exists and is not this caller's is not something a refusal is
/// allowed to reveal, and the difference between an attachment that was never
/// released and one whose release was taken back is a reading of the lifecycle
/// rather than an answer to a claim. The other two words say nothing about any
/// attachment: one names a key that already stands for a different set, and one
/// names a deployment that takes no new attachments at all.
/// </summary>
public enum AttachmentClaimStatus
{
    /// <summary>
    /// At least one reference in the set is not claimable by this caller now.
    /// Nothing was written and no other reference in the set was touched,
    /// because a claim accepts the whole set or changes nothing.
    /// <para>
    /// It is the value zero on purpose. A verdict nobody set is the verdict of
    /// a caller that never asked, and the one thing that must not be the
    /// default of this axis is the answer that lets a set through.
    /// </para>
    /// </summary>
    NotClaimable = 0,

    /// <summary>
    /// The same claim key already accepted a different set. It is not a
    /// repetition of an earlier claim, which answers with the set it accepted:
    /// it is two different sets asking to be the one that key stands for.
    /// </summary>
    ClaimKeyConflict,

    /// <summary>
    /// The whole set is claimed, and the outcome carries the snapshot of it.
    /// </summary>
    Claimed,

    /// <summary>
    /// This deployment takes no new attachments at all. Nothing in the set was
    /// found wanting: every reference resolved and belongs to this caller, no
    /// release was consulted, and no other set would have fared better here.
    /// <para>
    /// It is a word of its own beside <see cref="NotClaimable"/> because the
    /// two ask for opposite next steps. This one waits for whoever runs the
    /// service to switch the capability on, and the caller may send the very
    /// same set afterwards; the other one says this set may not be had, and
    /// sending it again changes nothing.
    /// </para>
    /// <para>
    /// It withholds everything the refusal above withholds. What it reveals is
    /// the deployment state of the capability, which is the same answer for
    /// every caller and for every reference, so learning it teaches nobody
    /// anything about an attachment that is not theirs.
    /// </para>
    /// <para>
    /// It sits at the end of the axis and never at the value zero. Zero stays
    /// with the refusal that lets no set through, which is what a verdict
    /// nobody set has to mean.
    /// </para>
    /// </summary>
    CapabilityNotEnabled,
}

/// <summary>
/// What one claim is asked for. Everything here is either an identifier the
/// caller owns or the opaque references it wants claimed; nothing describes an
/// attachment, because what an attachment is was settled before the claim and
/// is not the caller's to declare.
/// </summary>
public sealed record AttachmentClaimRequest
{
    /// <summary>The notification the claim is taken for.</summary>
    public required Guid NotificationId { get; init; }

    /// <summary>The application the notification belongs to.</summary>
    public required string Application { get; init; }

    /// <summary>
    /// The caller's idempotent key for this claim. The same key with the same
    /// set answers with the same claim; the same key with a different set is a
    /// conflict.
    /// </summary>
    public required string ClaimKey { get; init; }

    /// <summary>The set to claim, in the order the request declared it.</summary>
    public required AttachmentReferences References { get; init; }
}

/// <summary>
/// What one claim did. A claim that accepted the set carries the snapshot of
/// it; a claim that refused carries none, and the two cannot be confused
/// because there is no way to build an outcome that mixes them.
/// </summary>
public sealed record AttachmentClaimOutcome
{
    private AttachmentClaimOutcome(
        AttachmentClaimStatus status,
        AcceptedAttachmentSet? accepted)
    {
        Status = status;
        Accepted = accepted;
    }

    public AttachmentClaimStatus Status { get; }

    /// <summary>
    /// The set as it was accepted, or nothing when the claim refused. It is
    /// read-only on purpose: an outcome that could be rewritten member by
    /// member is an outcome whose refusal could be handed a snapshot after the
    /// fact.
    /// </summary>
    public AcceptedAttachmentSet? Accepted { get; }

    public static AttachmentClaimOutcome Claimed(AcceptedAttachmentSet accepted)
    {
        ArgumentNullException.ThrowIfNull(accepted);
        return new AttachmentClaimOutcome(AttachmentClaimStatus.Claimed, accepted);
    }

    /// <summary>
    /// Builds a refusal. It refuses to build one that says the set was
    /// claimed, because that outcome would report an acceptance with nothing
    /// accepted, and a caller reading the status alone would proceed.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The status names an acceptance.
    /// </exception>
    public static AttachmentClaimOutcome Refused(AttachmentClaimStatus status)
    {
        if (status == AttachmentClaimStatus.Claimed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "A refused claim never reports the set as claimed.");
        }

        return new AttachmentClaimOutcome(status, accepted: null);
    }
}

/// <summary>
/// Claims a whole set of attachments for one notification, inside the
/// transaction the caller already opened. The caller keeps its own persistence
/// to itself: it hands over the raw transaction and never a context, an entity
/// or a connection of its own making, and this module runs its own statements
/// on that transaction so the claim and whatever the caller is committing
/// become durable together or not at all.
/// <para>
/// The claim is indivisible. Every reference in the set is claimed or nothing
/// is written, which is what keeps a notification from ever being accepted
/// over a set it only partly holds.
/// </para>
/// <para>
/// The caller owns the transaction, the commit, the rollback and the disposal.
/// This module owns what it writes inside it, and it commits nothing.
/// </para>
/// </summary>
public interface IAttachmentClaim
{
    Task<AttachmentClaimOutcome> ClaimAsync(
        DbTransaction transaction,
        AttachmentClaimRequest request,
        CancellationToken cancellationToken);
}
