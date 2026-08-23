using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Masks the values of sensitive variables in a variables payload before the
/// masked render: scalar values become the fixed mask, containers keep their
/// shape with every leaf masked, and null stays null so optional-variable
/// behavior is preserved. The mask is irreversible on purpose: the stored form
/// proves that a value was sent, never which one.
/// </summary>
public static class VariableMasking
{
    public const string MaskedValue = "***";

    /// <summary>True when the payload carries at least one sensitive variable to mask.</summary>
    public static bool RequiresMasking(JsonElement? variables, IReadOnlyList<string> sensitiveVariables)
        => variables is { ValueKind: JsonValueKind.Object } payload
            && sensitiveVariables.Any(name => payload.TryGetProperty(name, out _));

    /// <summary>
    /// Returns the payload with every sensitive variable masked; a payload
    /// without sensitive variables comes back unchanged.
    /// </summary>
    public static JsonElement? MaskSensitiveVariables(JsonElement? variables, IReadOnlyList<string> sensitiveVariables)
    {
        if (!RequiresMasking(variables, sensitiveVariables))
        {
            return variables;
        }

        JsonObject root = JsonNode.Parse(variables!.Value.GetRawText())!.AsObject();
        foreach (var name in sensitiveVariables)
        {
            if (root.ContainsKey(name))
            {
                root[name] = Mask(root[name]);
            }
        }

        return JsonSerializer.SerializeToElement(root);
    }

    private static JsonNode? Mask(JsonNode? node) => node switch
    {
        null => null,
        JsonObject nested => new JsonObject(nested.Select(property =>
            KeyValuePair.Create(property.Key, Mask(property.Value)))),
        JsonArray items => new JsonArray([.. items.Select(Mask)]),
        _ => JsonValue.Create(MaskedValue),
    };
}
