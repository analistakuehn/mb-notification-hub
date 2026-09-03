using System.Collections;

namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// One attachment as it stood when the claim accepted it. Every member is a
/// value that was already settled by then, so nothing here has to be read
/// again to know what was accepted.
/// <para>
/// What is deliberately absent is the proof of the bytes. The algorithm and
/// the digest that say which bytes these are stay on the generation record
/// inside this module, and <see cref="ContentIdentity"/> is the handle that
/// reaches them: this module resolves it back to that record and compares
/// there, so the comparison happens on this side of the boundary and only its
/// verdict crosses. A digest carried out here would be the same statement
/// published in a form every consumer, every message and every log line could
/// copy, and it would have to be kept in step with a record it no longer sits
/// beside.
/// </para>
/// <para>
/// The same absence covers the coordinates. Nothing here names a store, a key,
/// a generation of the provider, a managed key, an address or anything that
/// could be exchanged for the content.
/// </para>
/// </summary>
public sealed record AcceptedAttachment
{
    /// <summary>
    /// Stands in for the members in any text rendering. A record prints every
    /// public member it has, and three of these are values this module does
    /// not put on a log line: the released name, the released media type and
    /// the released length. The reference stays, because it is the opaque
    /// correlator this module already logs by design and the one thing that
    /// makes a rendering worth reading.
    /// </summary>
    public const string Redacted = "accepted-attachment";

    /// <summary>The opaque identity of the attachment, as the claim received it.</summary>
    public required string Reference { get; init; }

    /// <summary>
    /// Which content was accepted, as a handle only this module can resolve.
    /// It changes when the accepted bytes would be different ones, and it says
    /// nothing on its own: it is neither a digest, nor a coordinate, nor a
    /// value a consumer can compare against anything but itself.
    /// </summary>
    public required string ContentIdentity { get; init; }

    /// <summary>The file name the release was granted over.</summary>
    public required string Name { get; init; }

    /// <summary>The media type the release was granted over.</summary>
    public required string MediaType { get; init; }

    /// <summary>The length, in bytes, the release was granted over.</summary>
    public required long Length { get; init; }

    public override string ToString() => Redacted + " " + Reference;
}

/// <summary>
/// The whole set a claim accepted, frozen in the order it was claimed in.
/// <para>
/// It freezes identity and composition: which attachments make up the set,
/// in which order, which content each of them was accepted with, and the name,
/// the media type and the length each was released under. It freezes no
/// eligibility at all. Whether a release is still in force, whether it was
/// taken back and whether it is still within its validity are read again,
/// every time, and a set that was accepted is not a set that may be sent.
/// </para>
/// <para>
/// It is immutable, it is compared by content, and it is never empty. A claim
/// is indivisible: it accepts the whole set or it changes nothing, so a set
/// with no members is not the outcome of a claim that accepted nothing, it is
/// a value nobody should have been able to build.
/// </para>
/// </summary>
public sealed class AcceptedAttachmentSet
    : IReadOnlyList<AcceptedAttachment>, IEquatable<AcceptedAttachmentSet>
{
    private readonly AcceptedAttachment[] _items;

    private AcceptedAttachmentSet(AcceptedAttachment[] items) => _items = items;

    public int Count => _items.Length;

    public AcceptedAttachment this[int index] => _items[index];

    /// <summary>
    /// Copies the given items into a snapshot nothing can change afterwards.
    /// <para>
    /// The refusals are the shapes the document that stores this snapshot
    /// cannot hold: an empty set, a blank member, a negative length and a
    /// reference repeated by ordinal comparison all read back as a document
    /// nobody can trust. Refusing them where the snapshot is built is what
    /// keeps that reader from ever meeting one.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The sequence is empty, carries a blank member or a negative length, or
    /// repeats a reference.
    /// </exception>
    public static AcceptedAttachmentSet Of(IEnumerable<AcceptedAttachment> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        AcceptedAttachment[] accepted = [.. items];
        if (accepted.Length == 0)
        {
            throw new ArgumentException(
                "An accepted attachment set carries at least one attachment.",
                nameof(items));
        }

        if (Array.Exists(accepted, item => !IsUsable(item)))
        {
            throw new ArgumentException(
                "An accepted attachment names a reference, a content identity, "
                    + "a name, a media type and a length that is not negative.",
                nameof(items));
        }

        if (accepted.Select(item => item.Reference).Distinct(StringComparer.Ordinal).Count()
            != accepted.Length)
        {
            throw new ArgumentException(
                "An accepted attachment set names each reference once.",
                nameof(items));
        }

        return new AcceptedAttachmentSet(accepted);
    }

    public bool Equals(AcceptedAttachmentSet? other)
        => other is not null
            && (ReferenceEquals(this, other) || _items.AsSpan().SequenceEqual(other._items));

    public override bool Equals(object? obj) => Equals(obj as AcceptedAttachmentSet);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (AcceptedAttachment item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<AcceptedAttachment> GetEnumerator()
        => ((IEnumerable<AcceptedAttachment>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    private static bool IsUsable(AcceptedAttachment? item)
        => item is not null
            && !string.IsNullOrWhiteSpace(item.Reference)
            && !string.IsNullOrWhiteSpace(item.ContentIdentity)
            && !string.IsNullOrWhiteSpace(item.Name)
            && !string.IsNullOrWhiteSpace(item.MediaType)
            && item.Length >= 0;
}
