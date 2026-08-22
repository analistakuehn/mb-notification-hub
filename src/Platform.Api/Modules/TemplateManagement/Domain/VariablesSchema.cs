using System.Text.Json;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>One variable declared by the version's variables schema.</summary>
public sealed record VariableDeclaration(string Name, bool Required, bool IsUrl);

/// <summary>
/// Reads the variable declarations out of the JSON Schema stored on a version:
/// the property names, which of them the schema marks as required, and which
/// carry a URL format and therefore fall under the link-domain allowlist.
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
                HasUrlFormat(property.Value)));
        }

        return declarations;
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

    private static bool HasUrlFormat(JsonElement declaration)
        => declaration.ValueKind == JsonValueKind.Object
            && declaration.TryGetProperty("format", out JsonElement format)
            && format.ValueKind == JsonValueKind.String
            && UrlFormats.Contains(format.GetString(), StringComparer.OrdinalIgnoreCase);
}
