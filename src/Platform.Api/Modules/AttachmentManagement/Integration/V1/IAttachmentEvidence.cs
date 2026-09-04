namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// What this module can still prove about the content one attachment was
/// accepted with, for a reader entitled to reconstruct what happened.
/// <para>
/// It is a different surface from the accepted snapshot, and the difference is
/// the whole reason this type exists. The snapshot travels with every dispatch,
/// every message and every log line that renders one, so it carries no proof of
/// the bytes at all: a digest published there would be the same statement in a
/// form anything could copy. This one is reached through a single authorized
/// read, so the proof travels here, where an auditor asking which bytes went
/// out has one answer and does not have to be given a way to fetch them.
/// </para>
/// <para>
/// The coordinates stay behind. The store, the key and the generation of the
/// provider are capacity to reach bytes rather than proof of them, and no
/// reader of evidence needs one: the digest, the algorithm and the length say
/// which bytes these were, and the path that opens content is a surface of its
/// own with an authorization of its own.
/// </para>
/// <para>
/// The record outlives the bytes. A sweep that takes the content of an
/// abandoned attachment removes no row, so this answer survives the removal and
/// <see cref="State"/> is what says the content is gone.
/// </para>
/// </summary>
public sealed record AttachmentEvidence
{
    /// <summary>
    /// Stands in for the members in any text rendering. A record prints every
    /// public member it has, and the digest is one of them: it is a fingerprint
    /// of the exact bytes, and the module that computes it keeps it out of
    /// every rendering for that reason. The handle stays, because it is the
    /// opaque correlator this module already logs by design.
    /// </summary>
    public const string Redacted = "attachment-evidence";

    /// <summary>The handle this answer was asked for, echoed back so a caller joins on it.</summary>
    public required string ContentIdentity { get; init; }

    /// <summary>
    /// The attachment the handle resolves to, as this module records it. It is
    /// this module's own answer and never a value taken from whoever asked, so
    /// a caller holding a snapshot compares the two and sees a disagreement
    /// instead of inheriting one.
    /// </summary>
    public required string Reference { get; init; }

    /// <summary>The producer application the attachment was registered under.</summary>
    public required string Application { get; init; }

    /// <summary>The state the attachment carries now.</summary>
    public required string State { get; init; }

    /// <summary>
    /// Which check refused, or which verdict did not conclude. Null while
    /// nothing was decided about the content and after a release cleared it.
    /// </summary>
    public string? ValidationDetail { get; init; }

    /// <summary>Registered name of the digest that was computed over these bytes.</summary>
    public required string DigestAlgorithm { get; init; }

    /// <summary>
    /// The digest of the exact generation the handle names, in lowercase hex.
    /// It is read off the record that was written when the bytes were captured
    /// and is never recomputed here: this surface opens no content, and a
    /// second computation of one digest turns a mismatch into an argument about
    /// which of the two is right.
    /// </summary>
    public required string Digest { get; init; }

    /// <summary>The length, in bytes, measured in the pass that measured the digest.</summary>
    public required long DigestedLengthBytes { get; init; }

    /// <summary>
    /// What the leading bytes were recognized as, or nothing when no signature
    /// matched them. It is a measurement of these exact bytes taken in that
    /// same pass, and never a declaration a producer wrote.
    /// </summary>
    public string? DetectedContentType { get; init; }

    /// <summary>When the bytes behind this handle were captured and measured.</summary>
    public required DateTimeOffset CapturedAt { get; init; }

    /// <summary>
    /// When the release over this exact generation was granted, or nothing when
    /// no grant ever named it. The grant that names the accepted generation is
    /// the one that answers here, and never the latest grant of the attachment:
    /// a revalidation writes a second row over other bytes, and reporting it
    /// would date the approval of content this notification never carried.
    /// </summary>
    public DateTimeOffset? ReleasedAt { get; init; }

    /// <summary>When that grant was taken back, or nothing when it was not.</summary>
    public DateTimeOffset? RevokedAt { get; init; }

    /// <summary>Why it was taken back, as the caller declared it.</summary>
    public string? RevocationReason { get; init; }

    public override string ToString() => Redacted + " " + ContentIdentity;
}

/// <summary>
/// Answers what this module still holds about the content behind accepted
/// attachments, for the one read that reconstructs a notification.
/// <para>
/// The handle is the join and there is no second one. The snapshot on the
/// notification row names the content by the opaque handle this module minted,
/// only this module resolves it, and a reader that keyed on anything else would
/// be asking about an attachment rather than about the bytes the notification
/// was accepted with.
/// </para>
/// <para>
/// It reads and never writes. The aggregate carries a row version, so a write
/// from here would invalidate the reading of anything that had loaded the row
/// first, and an evidence read has no reason to change what it describes.
/// </para>
/// </summary>
public interface IAttachmentEvidence
{
    /// <summary>
    /// What this module records for each handle it still answers for, keyed by
    /// the handle exactly as it was asked about.
    /// <para>
    /// A handle this module no longer answers for is absent from the answer
    /// rather than present and empty, and the caller says so in its own terms.
    /// Text that names no generation at all leaves the same way, because a
    /// handle that cannot be resolved names nothing whatever the reason.
    /// </para>
    /// </summary>
    Task<IReadOnlyDictionary<string, AttachmentEvidence>> DescribeAcceptedContentAsync(
        IReadOnlyCollection<string> contentIdentities,
        CancellationToken cancellationToken);
}
