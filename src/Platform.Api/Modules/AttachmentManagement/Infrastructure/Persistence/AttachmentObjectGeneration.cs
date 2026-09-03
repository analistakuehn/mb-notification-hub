using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// One generation of attachment bytes that was effectively captured, with the
/// measurement taken while reading that generation back. A row is written
/// complete, and a second generation under the same key becomes a second row,
/// so no capture overwrites the record of an earlier one.
/// <para>
/// Nothing in this module revises a row, and the mapping refuses the two
/// revisions that travel through the change tracker. It refuses neither of the
/// other two: an update of a detached instance is dropped in silence, and a
/// set-based update rewrites the durable value. Not being revised is therefore
/// what this module does, not what the storage enforces, and it stays that way
/// until the database itself refuses those two.
/// </para>
/// </summary>
internal sealed class AttachmentObjectGeneration
{
    internal const int MaxAlgorithmLength = 20;

    /// <summary>
    /// Room for a type the signature table names. The table is closed and its
    /// entries are ordinary media types, so this is the width of a name and
    /// never of a declaration a producer wrote.
    /// </summary>
    internal const int MaxDetectedContentTypeLength = 100;

    // EF Core materialization: properties are populated from the store.
    private AttachmentObjectGeneration()
    {
        Store = null!;
        Key = null!;
        Version = null!;
        Algorithm = null!;
        Digest = null!;
    }

    internal Guid Id { get; private set; }

    internal Guid AttachmentId { get; private set; }

    internal string Store { get; private set; }

    internal string Key { get; private set; }

    internal string Version { get; private set; }

    internal string Algorithm { get; private set; }

    internal byte[] Digest { get; private set; }

    internal long LengthBytes { get; private set; }

    /// <summary>
    /// What the leading bytes of this generation were recognized as, or
    /// nothing when no signature matched them. It is a measurement of these
    /// exact bytes, taken in the pass that measured the digest, and it is as
    /// frozen as the digest is: a later reading of the policy reads what was
    /// measured then, not what a stream would say now.
    /// </summary>
    internal string? DetectedContentType { get; private set; }

    internal DateTimeOffset CapturedAt { get; private set; }

    internal static AttachmentObjectGeneration Capture(
        Guid attachmentId,
        AttachmentObjectLocator locator,
        AttachmentContentProof proof,
        string? detectedContentType,
        DateTimeOffset capturedAt)
        => new()
        {
            Id = Guid.CreateVersion7(),
            AttachmentId = attachmentId,
            Store = locator.Store,
            Key = locator.Key,
            Version = locator.Version,
            Algorithm = proof.Algorithm,
            Digest = proof.Digest.ToArray(),
            LengthBytes = proof.LengthBytes,
            DetectedContentType = detectedContentType,
            CapturedAt = capturedAt,
        };

    internal AttachmentObjectLocator Locator()
        => AttachmentObjectLocator.FromStoredRow(Store, Key, Version);

    internal AttachmentContentProof Proof()
        => AttachmentContentProof.Recorded(Algorithm, Digest, LengthBytes);
}
