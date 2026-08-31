using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// SHA-256 over a canonical byte representation. The canonical form is
/// insertion-order independent (entries sorted by channel then locale) and
/// distinguishes absent fields from empty ones. Each field is length-prefixed
/// (<c>A</c> for an absent field, <c>V{utf8ByteCount}:{value}</c> for a present
/// one), so no field value can forge a field boundary and the same logical
/// content always produces the same hash.
/// </summary>
internal static class CanonicalHash
{
    private const char RecordSeparator = (char)0x1E;

    // Separates the names inside the sensitive-variable field. A name is
    // letters, digits, underscores and dots by construction, so no name can
    // carry this character and no pair of names can forge a third one.
    private const char UnitSeparator = (char)0x1F;

    internal static string OfFields(params string?[] fields)
        => Hash(AppendFields(new StringBuilder(), fields).ToString());

    /// <summary>
    /// Hash of a version, over the canonical form of its variables schema
    /// rather than over the schema as it was submitted. The caller produces
    /// that form, because producing it is the step that can refuse the document
    /// and this type answers on every input it is given: a hash that could fail
    /// would put the refusal in the one place with no way to report it.
    /// </summary>
    internal static string OfVersion(
        string? canonicalVariablesSchema,
        string? layoutKey,
        int? layoutVersion,
        IReadOnlyList<string> sensitiveVariables,
        IEnumerable<TemplateContent> contents)
    {
        var builder = new StringBuilder();
        var sensitive = CanonicalSensitiveVariables(sensitiveVariables);

        // The layout fields join the header record only when a layout is
        // pinned, so versions without one keep their historical hash bytes.
        // The sensitive-variable field is not treated that way: it is always
        // present, empty included, so a version that declares nothing is
        // distinguishable from one that never carried the field at all. The
        // exemption above exists to preserve bytes already written, and no
        // stored version predates this field.
        if (layoutKey is null)
        {
            AppendFields(builder, canonicalVariablesSchema, sensitive).Append(RecordSeparator);
        }
        else
        {
            AppendFields(
                builder,
                canonicalVariablesSchema,
                layoutKey,
                layoutVersion!.Value.ToString(CultureInfo.InvariantCulture),
                sensitive).Append(RecordSeparator);
        }

        IOrderedEnumerable<TemplateContent> ordered = contents
            .OrderBy(content => content.Channel.Value, StringComparer.Ordinal)
            .ThenBy(content => content.Locale.Value, StringComparer.Ordinal);
        foreach (TemplateContent content in ordered)
        {
            AppendFields(
                builder,
                content.Channel.Value,
                content.Locale.Value,
                content.Subject,
                content.Body,
                content.BodyText).Append(RecordSeparator);
        }

        return Hash(builder.ToString());
    }

    internal static string OfLayoutVersion(IEnumerable<LayoutContent> contents)
    {
        var builder = new StringBuilder();
        IOrderedEnumerable<LayoutContent> ordered = contents
            .OrderBy(content => content.Channel.Value, StringComparer.Ordinal)
            .ThenBy(content => content.Locale.Value, StringComparer.Ordinal);
        foreach (LayoutContent content in ordered)
        {
            AppendFields(
                builder,
                content.Channel.Value,
                content.Locale.Value,
                content.Body,
                content.BodyText).Append(RecordSeparator);
        }

        return Hash(builder.ToString());
    }

    /// <summary>
    /// The declared sensitive names as one field. Sorted, because the mask and
    /// the publication check both read the declaration as a set: two versions
    /// that name the same variables must hash alike however the author typed
    /// the order, exactly as the content entries below are ordered rather than
    /// hashed as written.
    /// </summary>
    private static string CanonicalSensitiveVariables(IReadOnlyList<string> sensitiveVariables)
        => string.Join(
            UnitSeparator,
            sensitiveVariables.OrderBy(variable => variable, StringComparer.Ordinal));

    private static StringBuilder AppendFields(StringBuilder builder, params string?[] fields)
    {
        foreach (var field in fields)
        {
            if (field is null)
            {
                builder.Append('A');
            }
            else
            {
                builder.Append('V')
                    .Append(Encoding.UTF8.GetByteCount(field).ToString(CultureInfo.InvariantCulture))
                    .Append(':')
                    .Append(field);
            }
        }

        return builder;
    }

    private static string Hash(string canonical)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
