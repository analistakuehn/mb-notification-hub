using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// SHA-256 over a canonical byte representation. The canonical form is
/// insertion-order independent (entries sorted by channel then locale) and
/// distinguishes absent fields from empty ones, so the same logical content
/// always produces the same hash.
/// </summary>
internal static class CanonicalHash
{
    private const char FieldSeparator = (char)0x1F;
    private const char RecordSeparator = (char)0x1E;
    private const char AbsentMarker = (char)0x00;

    internal static string OfFields(params string?[] fields)
        => Hash(AppendFields(new StringBuilder(), fields).ToString());

    internal static string OfVersion(
        string? variablesSchemaJson,
        string? layoutKey,
        int? layoutVersion,
        IEnumerable<TemplateContent> contents)
    {
        var builder = new StringBuilder();
        var canonicalSchema = variablesSchemaJson is null
            ? null
            : CanonicalJson.Normalize(variablesSchemaJson);

        // The layout fields join the header record only when a layout is
        // pinned, so versions without one keep their historical hash bytes.
        if (layoutKey is null)
        {
            AppendFields(builder, canonicalSchema).Append(RecordSeparator);
        }
        else
        {
            AppendFields(
                builder,
                canonicalSchema,
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
                builder.Append(AbsentMarker);
            }
            else
            {
                builder.Append(field);
            }

            builder.Append(FieldSeparator);
        }

        return builder;
    }

    private static string Hash(string canonical)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
}
