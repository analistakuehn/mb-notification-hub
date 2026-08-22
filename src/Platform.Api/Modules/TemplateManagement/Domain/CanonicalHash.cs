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

    internal static string OfVersion(string? variablesSchemaJson, IEnumerable<TemplateContent> contents)
    {
        var builder = new StringBuilder();
        string? canonicalSchema = variablesSchemaJson is null
            ? null
            : CanonicalJson.Normalize(variablesSchemaJson);
        AppendFields(builder, canonicalSchema).Append(RecordSeparator);

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

    private static StringBuilder AppendFields(StringBuilder builder, params string?[] fields)
    {
        foreach (string? field in fields)
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
