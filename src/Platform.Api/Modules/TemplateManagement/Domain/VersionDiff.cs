using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>The named fields of one content entry, keyed by canonical field name.</summary>
public sealed record ContentFieldSet(
    string Channel,
    string Locale,
    IReadOnlyDictionary<string, string?> Fields);

/// <summary>A content entry present in both versions whose fields differ.</summary>
public sealed record ContentChange(string Channel, string Locale, IReadOnlyList<string> ChangedFields);

/// <summary>Structural difference between the content sets of two versions.</summary>
public sealed record ContentSetDiff(
    IReadOnlyList<ContentUnit> Added,
    IReadOnlyList<ContentUnit> Removed,
    IReadOnlyList<ContentChange> Changed);

/// <summary>Field-level difference between the variables schemas of two versions.</summary>
public sealed record SchemaFieldDiff(
    IReadOnlyList<string> AddedFields,
    IReadOnlyList<string> RemovedFields,
    IReadOnlyList<string> ChangedFields);

/// <summary>
/// Read-only structural comparison between two immutable versions: which
/// (channel, locale) entries were added, removed or changed, and which
/// variables-schema fields were added, removed or changed. The comparison is
/// deterministic: entries are ordered by channel then locale, fields by name.
/// </summary>
public static class VersionDiff
{
    /// <summary>
    /// Compares the content set of the base version against the one it is
    /// diffed against: added means present in base only, removed means present
    /// in the other version only.
    /// </summary>
    public static ContentSetDiff DiffContents(
        IReadOnlyList<ContentFieldSet> baseContents,
        IReadOnlyList<ContentFieldSet> againstContents)
    {
        ArgumentNullException.ThrowIfNull(baseContents);
        ArgumentNullException.ThrowIfNull(againstContents);

        var baseByUnit = baseContents
            .ToDictionary(content => new ContentUnit(content.Channel, content.Locale));
        var againstByUnit = againstContents
            .ToDictionary(content => new ContentUnit(content.Channel, content.Locale));

        var added = baseByUnit.Keys
            .Where(unit => !againstByUnit.ContainsKey(unit))
            .OrderBy(unit => unit.Channel, StringComparer.Ordinal)
            .ThenBy(unit => unit.Locale, StringComparer.Ordinal)
            .ToList();
        var removed = againstByUnit.Keys
            .Where(unit => !baseByUnit.ContainsKey(unit))
            .OrderBy(unit => unit.Channel, StringComparer.Ordinal)
            .ThenBy(unit => unit.Locale, StringComparer.Ordinal)
            .ToList();

        List<ContentChange> changed = [];
        foreach (ContentUnit unit in baseByUnit.Keys
            .Where(againstByUnit.ContainsKey)
            .OrderBy(unit => unit.Channel, StringComparer.Ordinal)
            .ThenBy(unit => unit.Locale, StringComparer.Ordinal))
        {
            List<string> changedFields = ChangedFields(baseByUnit[unit].Fields, againstByUnit[unit].Fields);
            if (changedFields.Count > 0)
            {
                changed.Add(new ContentChange(unit.Channel, unit.Locale, changedFields));
            }
        }

        return new ContentSetDiff(added, removed, changed);
    }

    /// <summary>
    /// Compares the declared fields of two variables schemas. A field counts as
    /// changed when its canonical declaration differs or when its membership in
    /// the schema's required list flips. A schema that is absent or unreadable
    /// contributes no fields, so its counterpart's fields surface as added or
    /// removed instead of failing the diff.
    /// </summary>
    public static SchemaFieldDiff DiffVariablesSchemas(string? baseSchemaJson, string? againstSchemaJson)
    {
        Dictionary<string, string> baseFields = ReadFieldDeclarations(baseSchemaJson);
        Dictionary<string, string> againstFields = ReadFieldDeclarations(againstSchemaJson);

        var added = baseFields.Keys
            .Where(name => !againstFields.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToList();
        var removed = againstFields.Keys
            .Where(name => !baseFields.ContainsKey(name))
            .Order(StringComparer.Ordinal)
            .ToList();
        var changed = baseFields.Keys
            .Where(name => againstFields.TryGetValue(name, out var declaration)
                && !string.Equals(declaration, baseFields[name], StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

        return new SchemaFieldDiff(added, removed, changed);
    }

    private static List<string> ChangedFields(
        IReadOnlyDictionary<string, string?> baseFields,
        IReadOnlyDictionary<string, string?> againstFields)
        => baseFields.Keys
            .Union(againstFields.Keys, StringComparer.Ordinal)
            .Where(field => !string.Equals(
                baseFields.GetValueOrDefault(field),
                againstFields.GetValueOrDefault(field),
                StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Field name mapped to its canonical declaration plus required flag.</summary>
    private static Dictionary<string, string> ReadFieldDeclarations(string? schemaJson)
    {
        Dictionary<string, string> declarations = new(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return declarations;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            JsonElement root = document.RootElement;
            HashSet<string> required = ReadRequired(root);
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("properties", out JsonElement properties)
                || properties.ValueKind != JsonValueKind.Object)
            {
                return declarations;
            }

            foreach (JsonProperty property in properties.EnumerateObject())
            {
                var requiredMarker = required.Contains(property.Name) ? "required:" : "optional:";
                declarations[property.Name] = requiredMarker + CanonicalJson.Normalize(property.Value.GetRawText());
            }

            return declarations;
        }
        catch (JsonException)
        {
            return declarations;
        }
    }

    private static HashSet<string> ReadRequired(JsonElement root)
    {
        HashSet<string> required = new(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("required", out JsonElement names)
            || names.ValueKind != JsonValueKind.Array)
        {
            return required;
        }

        foreach (JsonElement name in names.EnumerateArray())
        {
            if (name.ValueKind == JsonValueKind.String)
            {
                required.Add(name.GetString()!);
            }
        }

        return required;
    }
}
