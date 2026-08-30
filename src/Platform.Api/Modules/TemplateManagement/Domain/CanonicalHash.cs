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
        IEnumerable<TemplateContent> contents)
    {
        var builder = new StringBuilder();

        // The layout fields join the header record only when a layout is
        // pinned, so versions without one keep their historical hash bytes.
        if (layoutKey is null)
        {
            AppendFields(builder, canonicalVariablesSchema).Append(RecordSeparator);
        }
        else
        {
            AppendFields(
                builder,
                canonicalVariablesSchema,
                layoutKey,
                layoutVersion!.Value.ToString(CultureInfo.InvariantCulture)).Append(RecordSeparator);
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
