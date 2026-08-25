using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Masks the values of sensitive variables in a variables payload before it is
/// stored as the query and audit projection: scalar values become the fixed
/// mask, containers keep their shape with every leaf masked, and null stays
/// null so optional-variable behavior is preserved. The mask is irreversible
/// on purpose: the stored projection proves that a value was sent, never
/// which one. Only the encrypted envelope keeps the full object.
/// </summary>
internal static class VariablesMask
{
    internal const string MaskedValue = "***";

    /// <summary>
    /// Canonical JSON of <paramref name="variables"/> with every variable named
    /// in <paramref name="sensitiveVariables"/> masked; an absent payload
    /// becomes an empty object, because the stored projection is mandatory.
    /// </summary>
    internal static string MaskedProjection(JsonElement? variables, IReadOnlyList<string> sensitiveVariables)
    {
        if (variables is not { ValueKind: JsonValueKind.Object } payload)
        {
            return "{}";
        }

        JsonObject root = JsonNode.Parse(payload.GetRawText())!.AsObject();
        foreach (var name in sensitiveVariables)
        {
            if (root.ContainsKey(name))
            {
                root[name] = Mask(root[name]);
            }
        }

        using var masked = JsonDocument.Parse(root.ToJsonString());
        return Encoding.UTF8.GetString(CanonicalJson.CanonicalBytes(masked.RootElement));
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
