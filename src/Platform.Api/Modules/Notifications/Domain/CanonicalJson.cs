using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Deterministic JSON form used for hashing and for the stored variables
/// projections: compact, object keys sorted ordinally, duplicate keys
/// collapsed to the last occurrence, array order preserved, scalar tokens
/// written exactly as parsed. The writer pins
/// <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so the escaping
/// policy never shifts with runtime defaults; that is safe because the
/// canonical form only ever feeds hashes and storage, never HTML output.
/// <para>
/// Ordinal here means UTF-16 code-unit order, the order
/// <see cref="StringComparer.Ordinal"/> defines over the member name as a
/// string. It is not the order of the name's UTF-8 bytes, and the two disagree:
/// a key on a supplementary plane is a surrogate pair starting at U+D800, so it
/// sorts before U+E000, while its UTF-8 encoding starts at 0xF0 and sorts after
/// the 0xEE of U+E000. Sorting encoded names would be cheaper and would rewrite
/// the hash of every payload carrying such a key, so the string order is part
/// of the persisted contract rather than an implementation choice.
/// </para>
/// </summary>
internal static class CanonicalJson
{
    /// <summary>
    /// Members held before an object is written. Sizing this by hand is
    /// pointless past the first bucket the pool serves, so it names the
    /// smallest useful request and lets the pool round it up.
    /// </summary>
    private const int InitialMembers = 8;

    /// <summary>Canonical UTF-8 bytes of <paramref name="element"/>.</summary>
    internal static byte[] CanonicalBytes(JsonElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteCanonical(element, writer);
        }

        return buffer.WrittenSpan.ToArray();
    }

    internal static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                WriteMembersInOrder(element, writer);
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement item in element.EnumerateArray())
                {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    /// <summary>
    /// Writes the members of an object in canonical order. The members are
    /// collected into an array borrowed from the pool and sorted in place,
    /// rather than inserted into a sorted dictionary: the dictionary costs a
    /// tree node per member on every object at every depth, and the payload
    /// this runs over is walked once per accepted request and again for every
    /// replay, so those nodes are the bulk of what canonicalization allocates.
    /// The order is identical because the comparison is the same one.
    /// </summary>
    private static void WriteMembersInOrder(JsonElement element, Utf8JsonWriter writer)
    {
        Member[] members = ArrayPool<Member>.Shared.Rent(InitialMembers);
        var count = 0;
        try
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (count == members.Length)
                {
                    members = Grown(members, count);
                }

                members[count] = new Member(property.Name, property.Value, count);
                count++;
            }

            // The sort is not stable, so arrival order is carried in the member
            // and breaks every tie. That makes the order total, which is what
            // lets the duplicate rule below read off a sorted run.
            members.AsSpan(0, count).Sort(default(MemberOrder));

            for (var i = 0; i < count; i++)
            {
                // A repeated key collapses to its last occurrence, which the
                // tie break placed at the end of its run.
                if (i + 1 < count
                    && string.Equals(members[i].Name, members[i + 1].Name, StringComparison.Ordinal))
                {
                    continue;
                }

                writer.WritePropertyName(members[i].Name);
                WriteCanonical(members[i].Value, writer);
            }
        }
        finally
        {
            // The array goes back to the pool and outlives this call there, so
            // it must not keep the names and the document alive with it.
            members.AsSpan(0, count).Clear();
            ArrayPool<Member>.Shared.Return(members);
        }
    }

    private static Member[] Grown(Member[] current, int count)
    {
        Member[] larger = ArrayPool<Member>.Shared.Rent(current.Length * 2);
        current.AsSpan(0, count).CopyTo(larger);
        current.AsSpan(0, count).Clear();
        ArrayPool<Member>.Shared.Return(current);
        return larger;
    }

    /// <summary>One member of an object, with the position it arrived at.</summary>
    private readonly struct Member(string name, JsonElement value, int arrival)
    {
        internal string Name { get; } = name;

        internal JsonElement Value { get; } = value;

        internal int Arrival { get; } = arrival;
    }

    /// <summary>
    /// Canonical order over members: the name under
    /// <see cref="string.CompareOrdinal(string, string)"/>, which is the
    /// comparison <see cref="StringComparer.Ordinal"/> performs, then arrival
    /// order so equal names keep the sequence the document had. A struct so the
    /// sort binds the comparison directly instead of dispatching per element.
    /// </summary>
    private readonly struct MemberOrder : IComparer<Member>
    {
        public int Compare(Member x, Member y)
        {
            var byName = string.CompareOrdinal(x.Name, y.Name);
            return byName != 0 ? byName : x.Arrival.CompareTo(y.Arrival);
        }
    }
}
