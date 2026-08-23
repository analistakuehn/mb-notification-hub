using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>One variable declared by the version's variables schema.</summary>
public sealed record VariableDeclaration(string Name, bool Required, bool IsUrl)
{
    /// <summary>Primitive JSON Schema type the declaration names, when it names one.</summary>
    public string? Type { get; init; }
}

/// <summary>
/// Reads the variable declarations out of the JSON Schema stored on a version:
/// the property names, which of them the schema marks as required, which carry
/// a URL format and therefore fall under the link-domain allowlist, and the
/// primitive type each one declares.
/// </summary>
public static class VariablesSchema
{
    private static readonly string[] UrlFormats = ["url", "uri"];

    public static bool TryParse(string? schemaJson, out IReadOnlyList<VariableDeclaration> declarations)
    {
        declarations = [];
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            declarations = ReadDeclarations(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<VariableDeclaration> ReadDeclarations(JsonElement root)
    {
        HashSet<string> required = ReadRequired(root);
        List<VariableDeclaration> declarations = [];
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("properties", out JsonElement properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return declarations;
        }

        foreach (JsonProperty property in properties.EnumerateObject())
        {
            declarations.Add(new VariableDeclaration(
                property.Name,
                required.Contains(property.Name),
                HasUrlFormat(property.Value))
            {
                Type = ReadType(property.Value),
            });
        }

        return declarations;
    }

    /// <summary>Single-type declarations only; a type union belongs to a newer vocabulary and is tolerated as untyped.</summary>
    private static string? ReadType(JsonElement declaration)
        => declaration.ValueKind == JsonValueKind.Object
            && declaration.TryGetProperty("type", out JsonElement type)
            && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

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

    private static bool HasUrlFormat(JsonElement declaration)
        => declaration.ValueKind == JsonValueKind.Object
            && declaration.TryGetProperty("format", out JsonElement format)
            && format.ValueKind == JsonValueKind.String
            && UrlFormats.Contains(format.GetString(), StringComparer.OrdinalIgnoreCase);
}
