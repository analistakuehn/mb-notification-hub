using System.Security.Cryptography;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// What one pinned generation is made of, measured while reading it back. The
/// digest never reaches a text rendering: it is a fingerprint that confirms a
/// guessed content.
/// </summary>
internal sealed record AttachmentContentProof
{
    /// <summary>Registered name of the only digest this module computes.</summary>
    internal const string Sha256 = "sha-256";

    private AttachmentContentProof(string algorithm, ReadOnlyMemory<byte> digest, long lengthBytes)
    {
        Algorithm = algorithm;
        Digest = digest;
        LengthBytes = lengthBytes;
    }

    internal string Algorithm { get; }

    internal ReadOnlyMemory<byte> Digest { get; }

    internal long LengthBytes { get; }

    internal static AttachmentContentProof Sha256Of(ReadOnlyMemory<byte> digest, long lengthBytes)
        => new(Sha256, digest, lengthBytes);

    internal static AttachmentContentProof Recorded(
        string algorithm,
        ReadOnlyMemory<byte> digest,
        long lengthBytes)
        => new(algorithm, digest, lengthBytes);

    /// <summary>
    /// Compares two proofs without letting the comparison time describe how
    /// far the digests agree.
    /// </summary>
    internal bool Matches(AttachmentContentProof other)
        => string.Equals(Algorithm, other.Algorithm, StringComparison.Ordinal)
            && LengthBytes == other.LengthBytes
            && CryptographicOperations.FixedTimeEquals(Digest.Span, other.Digest.Span);

    /// <summary>
    /// The one comparison this type has.
    /// <para>
    /// The synthesized one compared the memory region that carries the digest,
    /// which answers by segment and not by content, so two proofs over
    /// byte-identical digests held in different arrays came back different and
    /// hashed apart. That left the type with two comparisons that disagreed,
    /// and the free one was the easier to reach. Both now go through the
    /// fixed-time content comparison.
    /// </para>
    /// </summary>
    public bool Equals(AttachmentContentProof? other)
        => other is not null && Matches(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Algorithm, StringComparer.Ordinal);
        hash.Add(LengthBytes);
        hash.AddBytes(Digest.Span);
        return hash.ToHashCode();
    }

    public override string ToString() => $"{Algorithm}/{LengthBytes}";
}
