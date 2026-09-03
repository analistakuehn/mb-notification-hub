namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// One withdrawal of one release. It is a row of its own and not a column on
/// the release, because the release is written complete and never revised: a
/// revocation that reached into it would be the first revision of a line the
/// whole module treats as immutable, and the storage refuses two of the four
/// ways to perform one and no more than two.
/// <para>
/// The row names the release it took back, not just the attachment. A
/// revocation that named only the attachment would say that some approval was
/// withdrawn without saying which, and the point of a release line that can
/// hold more than one grant is that those two are not the same statement.
/// </para>
/// <para>
/// Nothing in this module revises a row here either, and the mapping refuses
/// the same two revisions the release and the generation rows refuse. It
/// refuses neither of the other two: an update of a detached instance is
/// dropped in silence, and a set-based update rewrites the durable value.
/// </para>
/// </summary>
internal sealed class AttachmentRevocation
{
    /// <summary>
    /// Room for the caller's reason. The width is the one the module already
    /// uses for a durable reason and for a durable validation detail, and it
    /// is a ceiling rather than a measurement: a reason longer than it is
    /// refused rather than cut down, because a truncated reason reads back as
    /// a sentence nobody wrote and the one reader of this column is someone
    /// asking why the content stopped being deliverable.
    /// <para>
    /// It is deliberately narrow. A reason this short is a phrase, and a
    /// phrase is what an operational reader wants; a paragraph would invite a
    /// caller to paste whatever it happens to know about the file into a
    /// column that leaves the module through an authorized query.
    /// </para>
    /// </summary>
    internal const int MaxReasonLength = 40;

    // EF Core materialization: properties are populated from the store.
    private AttachmentRevocation()
    {
        Reason = null!;
    }

    internal Guid Id { get; private set; }

    internal Guid AttachmentId { get; private set; }

    /// <summary>The exact grant this revocation took back.</summary>
    internal Guid ReleaseId { get; private set; }

    /// <summary>Why the release was taken back, as declared by the caller.</summary>
    internal string Reason { get; private set; }

    internal DateTimeOffset RevokedAt { get; private set; }

    internal static AttachmentRevocation Record(
        Guid attachmentId,
        Guid releaseId,
        string reason,
        DateTimeOffset revokedAt)
        => new()
        {
            Id = Guid.CreateVersion7(),
            AttachmentId = attachmentId,
            ReleaseId = releaseId,
            Reason = reason,
            RevokedAt = revokedAt,
        };

    /// <summary>
    /// Whether the durable state can hold this reason as written. A reason it
    /// cannot hold is not written short: the revocation does not happen at
    /// all, because a withdrawal nobody can explain is worse than a request
    /// the caller is told to send again.
    /// </summary>
    internal static bool IsUsableReason(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= MaxReasonLength;
}
