namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// What the check said about a set that was already accepted. Three answers,
/// and only one of them lets the set be used.
/// </summary>
public enum AttachmentReleaseVerdict
{
    /// <summary>
    /// The check did not conclude, so nothing is known about the set. It is
    /// not a statement that the set is unusable, it is the absence of a
    /// statement, and the caller treats it as a refusal for the same reason
    /// every other undecided verdict in this module is treated as one.
    /// <para>
    /// It is the value zero on purpose. A verdict nobody produced, and a
    /// stand-in that was never told what to answer, both read as this one, and
    /// the alternative is a default that clears a set for delivery because
    /// nobody asked.
    /// </para>
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// The check concluded and the set may not be used. At least one member
    /// carries no release in force: it was never released, its release was
    /// taken back, its release is past its validity, or the content it was
    /// accepted with is no longer the content behind it.
    /// <para>
    /// One word for the four, because the difference between them changes
    /// nothing the caller does. Which member and which of the four is on the
    /// durable record and reaches the operational side through the read that
    /// exists for it.
    /// </para>
    /// </summary>
    Withheld,

    /// <summary>
    /// The check concluded and every member of the set is still released,
    /// still within its validity, and still the content it was accepted with.
    /// <para>
    /// It says so as of the moment it was asked, and it is not a permission
    /// that survives being carried around. It is asked immediately before the
    /// call it protects, and nothing between the two makes it true again.
    /// </para>
    /// </summary>
    Deliverable,
}

/// <summary>
/// Reads whether a set that was accepted may still be used, immediately before
/// the call that cannot be taken back.
/// <para>
/// This is the other half of the snapshot. The snapshot froze which
/// attachments the notification carries and what each of them is; it froze no
/// permission, and this is where the permission is read. A caller that skipped
/// it would be delivering on a release that was taken back an hour after the
/// notification was accepted.
/// </para>
/// <para>
/// It answers from what this module has recorded, and it opens no content and
/// calls no provider. Reading the bytes back and proving they are the bytes
/// that were accepted belongs to the path that actually opens them, and it is
/// a heavier act than this one: this check is the gate that keeps that path
/// from being entered at all.
/// </para>
/// </summary>
public interface IAttachmentReleaseCheck
{
    Task<AttachmentReleaseVerdict> VerifyAsync(
        AcceptedAttachmentSet accepted,
        CancellationToken cancellationToken);
}
