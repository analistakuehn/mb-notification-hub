using System.Security.Cryptography;

namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// What the comparison said about the bytes one attempt actually put on the
/// wire. Three answers, and only one of them says the delivered set is the
/// released set.
/// </summary>
public enum AttachmentSubmissionVerdict
{
    /// <summary>
    /// Nothing was established: the handle names no generation this module
    /// recorded, the record is gone, or it could not be read. It is not a
    /// statement that the bytes diverged, it is the absence of a statement,
    /// and a reader that treated it as agreement would be reading silence as
    /// proof.
    /// <para>
    /// It is the value zero on purpose. A verdict nobody produced, and a
    /// stand-in that was never told what to answer, both read as this one, and
    /// the alternative is a default that certifies a submission nobody
    /// compared.
    /// </para>
    /// </summary>
    Unavailable = 0,

    /// <summary>
    /// At least one member left carrying bytes that are not the bytes its
    /// release was granted over: another digest, another length, or both.
    /// <para>
    /// It is a statement about a call that already happened. The bytes are
    /// gone by the time this can be known, because knowing it costs reading
    /// them, so this ends nothing and prevents nothing: it records that the
    /// promise made to the recipient was not kept on this attempt.
    /// </para>
    /// </summary>
    Divergent,

    /// <summary>
    /// Every member left carrying exactly the bytes its release was granted
    /// over, by digest and by length, measured on the bytes that were written
    /// rather than on the record that describes them.
    /// </summary>
    Matched,
}

/// <summary>
/// What one member of a set measured as it was written out: which content it
/// claims to be, how many bytes of it went out, and the digest of those exact
/// bytes.
/// <para>
/// It travels inwards and never outwards. The digest here is the caller's own
/// measurement of bytes the caller was holding anyway, so handing it over
/// publishes nothing the caller did not already have; what never crosses in
/// the other direction is the recorded digest, which stays on the generation
/// row where the comparison happens.
/// </para>
/// </summary>
public sealed record SubmittedAttachmentBytes
{
    /// <summary>
    /// Stands in for every member in any text rendering. A record prints each
    /// public member it has, and all three of these are values this module
    /// keeps off an operational line: the handle is producer-adjacent, and the
    /// digest and the length describe content.
    /// </summary>
    public const string Redacted = "submitted-attachment-bytes";

    /// <summary>
    /// Which content these bytes claim to be, as the handle the accepted set
    /// carried. It is resolved by the module that minted it and by nobody
    /// else.
    /// </summary>
    public required string ContentIdentity { get; init; }

    /// <summary>How many raw bytes of this member were written out.</summary>
    public required long Length { get; init; }

    /// <summary>
    /// The SHA-256 of those exact bytes, measured in the pass that wrote them.
    /// A second pass to measure it would be a second reading of the content,
    /// and two readings of a remote object are two chances to read different
    /// bytes.
    /// </summary>
    public required ReadOnlyMemory<byte> Digest { get; init; }

    public override string ToString() => Redacted;

    /// <summary>
    /// The one comparison this type has.
    /// <para>
    /// The synthesized one compares the memory region that carries the digest,
    /// which answers by segment and not by content, so two measurements over
    /// byte-identical digests held in different arrays come back different and
    /// hash apart. A published contract whose equality answered about buffers
    /// would report the submission a caller built and the submission it
    /// rebuilt as two different submissions forever.
    /// </para>
    /// <para>
    /// The digest goes through the fixed-time comparison, for the same reason
    /// the recorded proof inside the owning module does: how long the answer
    /// takes must not describe how far two digests agree.
    /// </para>
    /// </summary>
    public bool Equals(SubmittedAttachmentBytes? other)
        => other is not null
            && string.Equals(ContentIdentity, other.ContentIdentity, StringComparison.Ordinal)
            && Length == other.Length
            && CryptographicOperations.FixedTimeEquals(Digest.Span, other.Digest.Span);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ContentIdentity, StringComparer.Ordinal);
        hash.Add(Length);
        hash.AddBytes(Digest.Span);
        return hash.ToHashCode();
    }
}

/// <summary>
/// Settles the witness of one attempt: compares what was submitted against
/// what was released, on this side of the boundary, and records the outcome on
/// this module's own line.
/// <para>
/// The two sides of the comparison are deliberately produced by different
/// passes over different bytes. The released side is the digest recorded while
/// the upload was being captured, which is the value the whole release was
/// granted over; the submitted side is measured by the caller, at send time,
/// over the bytes the custody handed back and the caller wrote out. A witness
/// built from the recorded side alone would be the record compared with
/// itself, and it would answer that the bytes agree for any bytes at all.
/// </para>
/// <para>
/// Only the verdict comes out, which is what keeps the proof of the bytes
/// inside this module while the composition it describes is frozen outside it.
/// The caller learns whether the set it delivered is the set that was released
/// and learns nothing it could hand to anyone else.
/// </para>
/// <para>
/// It settles a call that has already happened. There is no reading of a
/// remote object cheap enough to do this before the bytes leave, because the
/// bytes leaving is what produces the measurement, so this answers about the
/// past and never gates the future.
/// </para>
/// </summary>
public interface IAttachmentSubmissionWitness
{
    /// <summary>
    /// Compares every submitted member against the generation its handle
    /// names, and answers for the whole submission.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The submission carries no member, or a member with no handle or a
    /// negative length.
    /// </exception>
    Task<AttachmentSubmissionVerdict> SettleAsync(
        IReadOnlyList<SubmittedAttachmentBytes> submitted,
        CancellationToken cancellationToken);
}
