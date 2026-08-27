using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace NotificationHub.SharedKernel;

/// <summary>
/// The single structural rule that decides which values of a variables payload
/// a mask replaces. Both stored forms of the same request read it, so they can
/// never disagree about what was sensitive: a divergence would reopen the leak
/// on one side only, with nothing to signal it.
/// <para>
/// A name without a dot matches a property with that name at any depth, in
/// objects and in array elements. A name with a dot is an absolute path from
/// the root and matches only there. Resolution is always by path and never by
/// literal key, because a top-level key that spells the path would otherwise
/// hand the producer a key name that leaves the mask in silence. Matching a
/// node masks it: a scalar becomes the fixed mask, a container keeps its shape
/// with every leaf masked, and null stays null, so an optional variable that
/// was not sent still reads as absent.
/// </para>
/// <para>
/// The walk reads <see cref="JsonElement"/> and writes a copy through
/// <see cref="Utf8JsonWriter"/> instead of rebuilding a node tree. A node tree
/// materializes its properties into a dictionary on the first lookup by key,
/// and a payload that repeats a key, which the web JSON defaults accept,
/// throws there and takes the whole request down.
/// </para>
/// </summary>
public static class SensitiveValueMask
{
    /// <summary>The irreversible replacement: the form proves a value was sent, never which one.</summary>
    public const string MaskedValue = "***";

    // Pinned so the escaping policy never shifts with runtime defaults. The
    // copy only ever feeds a re-parse, a hash and storage, never HTML output.
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The result of one application of the rule, in the three states a caller
    /// has to tell apart. <see cref="Changed"/> false with no refusal means the
    /// payload carried nothing to mask, and reusing the complete form there is
    /// correct. <see cref="RefusedName"/> is set when a proper prefix of a
    /// dotted name resolves to a node that is neither an object nor null: the
    /// rest of the name cannot address anything under it, and routing that to
    /// "nothing to mask" is exactly what would seal the complete form as the
    /// masked one.
    /// </summary>
    /// <param name="Value">The masked payload, or the original when nothing changed.</param>
    /// <param name="Changed">Whether at least one value was replaced by the mask.</param>
    /// <param name="RefusedName">The sensitive name whose path broke, when one did.</param>
    public readonly record struct Outcome(JsonElement Value, bool Changed, string? RefusedName)
    {
        /// <summary>Whether a sensitive name failed to address the shape of the payload.</summary>
        public bool IsRefused => RefusedName is not null;
    }

    /// <summary>Applies every sensitive name to <paramref name="payload"/> in one walk.</summary>
    public static Outcome Apply(JsonElement payload, IReadOnlyList<string> sensitiveNames)
    {
        ArgumentNullException.ThrowIfNull(sensitiveNames);
        if (payload.ValueKind != JsonValueKind.Object || sensitiveNames.Count == 0)
        {
            return new Outcome(payload, false, null);
        }

        HashSet<string>? names = null;
        List<PathCursor>? paths = null;
        foreach (var name in sensitiveNames)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            if (name.Contains('.', StringComparison.Ordinal))
            {
                paths ??= [];
                paths.Add(new PathCursor(name, name.Split('.'), 0));
            }
            else
            {
                names ??= new HashSet<string>(StringComparer.Ordinal);
                names.Add(name);
            }
        }

        if (names is null && paths is null)
        {
            return new Outcome(payload, false, null);
        }

        var buffer = new ArrayBufferWriter<byte>();
        var walk = new Walk(names);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            walk.WriteObject(payload, writer, paths);
        }

        if (!walk.Changed)
        {
            return new Outcome(payload, false, walk.RefusedName);
        }

        using var masked = JsonDocument.Parse(buffer.WrittenMemory);
        return new Outcome(masked.RootElement.Clone(), true, walk.RefusedName);
    }

    /// <summary>One dotted name, and how far into it the walk has already matched.</summary>
    private readonly record struct PathCursor(string Name, string[] Segments, int Index)
    {
        internal bool IsTerminus => Index == Segments.Length - 1;

        internal bool MatchesHere(string propertyName)
            => string.Equals(Segments[Index], propertyName, StringComparison.Ordinal);

        internal PathCursor Deeper() => this with { Index = Index + 1 };
    }

    /// <summary>
    /// One traversal that both copies and decides. Fusing them is what keeps
    /// the answer to "did anything change" derived from the same walk that
    /// changed it: computed apart, the two can disagree, and a "nothing
    /// changed" returned after masking retains the complete form with its hash
    /// intact, which no assertion about structure would catch.
    /// </summary>
    private sealed class Walk(HashSet<string>? names)
    {
        internal bool Changed { get; private set; }

        internal string? RefusedName { get; private set; }

        internal void WriteObject(JsonElement element, Utf8JsonWriter writer, List<PathCursor>? cursors)
        {
            if (names is null && cursors is null)
            {
                element.WriteTo(writer);
                return;
            }

            writer.WriteStartObject();
            foreach (JsonProperty property in element.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                WriteValue(property.Name, property.Value, writer, cursors);
            }

            writer.WriteEndObject();
        }

        private void WriteValue(
            string name,
            JsonElement value,
            Utf8JsonWriter writer,
            List<PathCursor>? cursors)
        {
            if (names is not null && names.Contains(name))
            {
                WriteMasked(value, writer);
                return;
            }

            List<PathCursor>? deeper = null;
            var terminus = false;
            if (cursors is not null)
            {
                foreach (PathCursor cursor in cursors)
                {
                    if (!cursor.MatchesHere(name))
                    {
                        continue;
                    }

                    if (cursor.IsTerminus)
                    {
                        terminus = true;
                    }
                    else
                    {
                        deeper ??= [];
                        deeper.Add(cursor.Deeper());
                    }
                }
            }

            if (terminus)
            {
                WriteMasked(value, writer);
                return;
            }

            if (deeper is not null && value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Null))
            {
                // A proper prefix of the name landed on a node the rest of the
                // name cannot address. The refusal travels to the caller, and
                // the node is masked anyway so the value stays out of whatever
                // the caller stores while it decides.
                RefusedName ??= deeper[0].Name;
                WriteMasked(value, writer);
                return;
            }

            WriteBranch(value, writer, deeper);
        }

        private void WriteBranch(JsonElement value, Utf8JsonWriter writer, List<PathCursor>? cursors)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    WriteObject(value, writer, cursors);
                    break;
                case JsonValueKind.Array:
                    WriteArray(value, writer);
                    break;
                default:
                    value.WriteTo(writer);
                    break;
            }
        }

        /// <summary>
        /// A dotted name never addresses an array position, so the cursors stop
        /// at the array; a name without a dot still matches inside its elements.
        /// </summary>
        private void WriteArray(JsonElement value, Utf8JsonWriter writer)
        {
            if (names is null)
            {
                value.WriteTo(writer);
                return;
            }

            writer.WriteStartArray();
            foreach (JsonElement item in value.EnumerateArray())
            {
                WriteBranch(item, writer, cursors: null);
            }

            writer.WriteEndArray();
        }

        private void WriteMasked(JsonElement value, Utf8JsonWriter writer)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (JsonProperty property in value.EnumerateObject())
                    {
                        writer.WritePropertyName(property.Name);
                        WriteMasked(property.Value, writer);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (JsonElement item in value.EnumerateArray())
                    {
                        WriteMasked(item, writer);
                    }

                    writer.WriteEndArray();
                    break;
                case JsonValueKind.Null:
                    writer.WriteNullValue();
                    break;
                default:
                    writer.WriteStringValue(MaskedValue);
                    Changed = true;
                    break;
            }
        }
    }
}
