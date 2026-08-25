using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>
/// Write surface of the immutable evidence store for a composer that produced
/// bytes it must be able to show years from now. The caller hands over the
/// content and receives a receipt; immutability, retention, the digest and the
/// refusal to overwrite stay inside this module, because they are what the
/// destination exists for and not options a caller may pick.
/// </summary>
public interface IEvidenceArchive
{
    /// <summary>
    /// Archives one object under <paramref name="key"/>, which is relative to
    /// the evidence prefix this module owns: the caller decides what
    /// identifies its evidence, this module decides where evidence lives.
    /// <para>
    /// A key that already holds exactly these bytes is left alone and the
    /// receipt says so, which is what makes a rerun free. A key that already
    /// holds different bytes is never overwritten and fails as an integration
    /// failure: archived evidence that no longer matches what the sources say
    /// is a finding for a human, not a retry for a job.
    /// </para>
    /// </summary>
    Task<Result<ArchivedEvidence>> ArchiveAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken);
}

/// <summary>
/// Receipt of one archived object: where it landed, what it hashes to, and
/// whether this round is what put it there.
/// </summary>
public sealed record ArchivedEvidence
{
    /// <summary>Full key inside the evidence store, prefix included.</summary>
    public required string Key { get; init; }

    /// <summary>Lowercase hex SHA-256 of the archived bytes.</summary>
    public required string Sha256Hex { get; init; }

    public required long Length { get; init; }

    /// <summary>True when the object was already there with exactly these bytes.</summary>
    public required bool AlreadyPresent { get; init; }
}
