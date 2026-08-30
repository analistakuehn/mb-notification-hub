using System.Text.Json;
using NotificationHub.SharedKernel;

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

            // A schema can parse and still not transcode, and every step of the
            // walk below transcodes: reading a property name, reading a string
            // value, and even looking a name up, because the lookup unescapes
            // candidate keys to compare them. The whole root is measured once,
            // before any of that, so this method keeps the promise its name
            // makes to the publication gate that is built on it.
            if (!CompactJsonSize.Measure(document.RootElement).IsReadable)
            {
                return false;
            }

            declarations = ReadDeclarations(document.RootElement);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The names in <paramref name="names"/> the schema does not declare, using
    /// the same addressing the mask uses: a name without a dot resolves against
    /// any depth the schema describes, including array elements, and a name
    /// with a dot resolves as an absolute path from the root.
    /// <para>
    /// The walk reads nested <c>properties</c> and <c>items</c> and nothing
    /// else. Resolving through <c>additionalProperties</c>, <c>$ref</c>,
    /// <c>oneOf</c> or <c>allOf</c> is refused rather than guessed: growing a
    /// JSON Schema implementation inside the domain to decide what a mask can
    /// reach would trade a loud refusal at publication for a silent one at
    /// render time.
    /// </para>
    /// </summary>
    /// <returns>False when the schema cannot be read, which is reported elsewhere.</returns>
    public static bool TryUndeclaredNames(
        string? schemaJson,
        IReadOnlyList<string> names,
        out IReadOnlyList<string> undeclared)
    {
        ArgumentNullException.ThrowIfNull(names);
        undeclared = [];
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            undeclared = [.. names];
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(schemaJson);
            if (!CompactJsonSize.Measure(document.RootElement).IsReadable)
            {
                return false;
            }

            undeclared = [.. names.Where(name => !Declares(document.RootElement, name))];
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool Declares(JsonElement schema, string name)
        => name.Contains('.', StringComparison.Ordinal)
            ? DeclaresPath(schema, name.Split('.'))
            : DeclaresAtAnyDepth(schema, name);

    private static bool DeclaresPath(JsonElement schema, string[] segments)
    {
        JsonElement level = schema;
        foreach (var segment in segments)
        {
            if (!TryProperties(level, out JsonElement properties)
                || !properties.TryGetProperty(segment, out JsonElement declaration))
            {
                return false;
            }

            level = declaration;
        }

        return true;
    }

    private static bool DeclaresAtAnyDepth(JsonElement schema, string name)
    {
        if (schema.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (TryProperties(schema, out JsonElement properties))
        {
            foreach (JsonProperty property in properties.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.Ordinal)
                    || DeclaresAtAnyDepth(property.Value, name))
                {
                    return true;
                }
            }
        }

        if (!schema.TryGetProperty("items", out JsonElement items))
        {
            return false;
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            return DeclaresAtAnyDepth(items, name);
        }

        foreach (JsonElement item in items.EnumerateArray())
        {
            if (DeclaresAtAnyDepth(item, name))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryProperties(JsonElement schema, out JsonElement properties)
    {
        properties = default;
        return schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out properties)
            && properties.ValueKind == JsonValueKind.Object;
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
