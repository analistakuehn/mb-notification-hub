using System.Collections;

namespace NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

/// <summary>
/// An ordered list of opaque attachment references, immutable once built and
/// compared by what it carries rather than by which instance carries it.
/// <para>
/// The order is part of the value. A claim is asked for over a set in the
/// order the request declared it, and the accepted set is frozen in that same
/// order, so two lists with the same references in a different order are two
/// different lists here and never one.
/// </para>
/// <para>
/// A reference is a string on this surface and nothing more. The shape a
/// reference takes is this module's own business, and publishing it would
/// invite a consumer to build one, which is exactly what an opaque identity
/// exists to prevent: the only references that reach here are references this
/// module handed out.
/// </para>
/// </summary>
public sealed class AttachmentReferences
    : IReadOnlyList<string>, IEquatable<AttachmentReferences>
{
    private readonly string[] _values;

    private AttachmentReferences(string[] values) => _values = values;

    public int Count => _values.Length;

    public string this[int index] => _values[index];

    /// <summary>
    /// Copies the given references into a value nothing can change afterwards.
    /// <para>
    /// Three refusals, and each of them describes a list that could not be
    /// claimed anyway. An empty list is not a claim over nothing, it is a
    /// caller that meant to send no manifest at all. Blank text is not an
    /// opaque identity. A reference repeated by ordinal comparison would
    /// freeze a set that names the same attachment twice, and the snapshot of
    /// such a set is unreadable by the rule that governs it.
    /// </para>
    /// <para>
    /// They throw rather than answer, because none of them is data a producer
    /// can send: the surface that admits a manifest refuses all three before a
    /// request is ever hashed, so a list that fails here is a defect on this
    /// side of the boundary.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The sequence is empty, carries blank text, or repeats a reference.
    /// </exception>
    public static AttachmentReferences Of(IEnumerable<string> references)
    {
        ArgumentNullException.ThrowIfNull(references);

        string[] values = [.. references];
        if (values.Length == 0)
        {
            throw new ArgumentException(
                "An attachment manifest names at least one reference.",
                nameof(references));
        }

        if (Array.Exists(values, string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException(
                "An attachment reference is never blank.",
                nameof(references));
        }

        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
        {
            throw new ArgumentException(
                "An attachment manifest names each reference once.",
                nameof(references));
        }

        return new AttachmentReferences(values);
    }

    public bool Equals(AttachmentReferences? other)
        => other is not null
            && (ReferenceEquals(this, other)
                || _values.AsSpan().SequenceEqual(other._values, StringComparer.Ordinal));

    public override bool Equals(object? obj) => Equals(obj as AttachmentReferences);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var value in _values)
        {
            hash.Add(value, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<string> GetEnumerator()
        => ((IEnumerable<string>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _values.GetEnumerator();
}
