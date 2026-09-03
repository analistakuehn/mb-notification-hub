namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// One grant of release over one generation of one attachment. A row is
/// written complete and never revised, in the same shape the generation record
/// already proved: a second release is a second row with an instant of its
/// own, so nothing that repeats can quietly extend the first one.
/// <para>
/// The row names the generation it released. A release that named only the
/// attachment would say that some bytes were approved without saying which,
/// and the whole point of the identity record is that those two are not the
/// same statement.
/// </para>
/// <para>
/// Nothing in this module revises a row, and the mapping refuses the two
/// revisions that travel through the change tracker. It refuses neither of the
/// other two: an update of a detached instance is dropped in silence, and a
/// set-based update rewrites the durable value. Not being revised is therefore
/// what this module does, not what the storage enforces, and it stays that way
/// until the database itself refuses those two.
/// </para>
/// </summary>
internal sealed class AttachmentRelease
{
    // EF Core materialization: properties are populated from the store.
    private AttachmentRelease()
    {
    }

    internal Guid Id { get; private set; }

    internal Guid AttachmentId { get; private set; }

    internal Guid GenerationId { get; private set; }

    internal DateTimeOffset ReleasedAt { get; private set; }

    /// <summary>
    /// The deadline this release was granted with, under the validity in force
    /// when it was granted. It is a record of the grant and not the comparison:
    /// the comparison is <see cref="DeadlineAt"/>, which reads the validity in
    /// force now, and the two say different things only after that value
    /// changes, which is exactly when the difference matters.
    /// </summary>
    internal DateTimeOffset ExpiresAt { get; private set; }

    internal static AttachmentRelease Grant(
        Guid attachmentId,
        Guid generationId,
        DateTimeOffset releasedAt,
        TimeSpan validity)
        => new()
        {
            Id = Guid.CreateVersion7(),
            AttachmentId = attachmentId,
            GenerationId = generationId,
            ReleasedAt = releasedAt,
            ExpiresAt = releasedAt + validity,
        };

    /// <summary>
    /// When this release stops being usable, counted from the later of the
    /// release and the instant the current validity took effect.
    /// <para>
    /// The second term is what makes a shorter validity safe to deploy. Counted
    /// from the release alone, cutting the value would expire, at the moment of
    /// the deployment, every release older than the new value, and every
    /// notification already accepted over those attachments would fail on its
    /// way out. Counted from the later of the two, nobody loses the new
    /// duration, and nothing that was accepted dies on the deployment itself.
    /// </para>
    /// </summary>
    internal DateTimeOffset DeadlineAt(TimeSpan validity, DateTimeOffset? effectiveFrom)
        => (effectiveFrom is { } from && from > ReleasedAt ? from : ReleasedAt) + validity;

    internal bool IsValidAt(DateTimeOffset now, TimeSpan validity, DateTimeOffset? effectiveFrom)
        => now < DeadlineAt(validity, effectiveFrom);
}
