using System.Text;
using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Masks the values of sensitive variables in a variables payload before it is
/// stored as the query and audit projection: scalar values become the fixed
/// mask, containers keep their shape with every leaf masked, and null stays
/// null so optional-variable behavior is preserved. The mask is irreversible
/// on purpose: the stored projection proves that a value was sent, never
/// which one. Only the encrypted envelope keeps the full object. Which nodes a
/// sensitive name addresses is decided by the shared structural rule, so this
/// projection and the masked render of the same request always mask the same
/// values.
/// </summary>
internal static class VariablesMask
{
    internal const string MaskedValue = SensitiveValueMask.MaskedValue;

    /// <summary>
    /// The masked payload plus the two facts the projection cannot carry:
    /// whether anything changed, and whether a sensitive name failed to address
    /// the shape of the payload.
    /// </summary>
    internal static SensitiveValueMask.Outcome Mask(
        JsonElement? variables,
        IReadOnlyList<string> sensitiveVariables)
        => variables is { ValueKind: JsonValueKind.Object } payload
            ? SensitiveValueMask.Apply(payload, sensitiveVariables)
            : default;

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

        SensitiveValueMask.Outcome outcome = SensitiveValueMask.Apply(payload, sensitiveVariables);
        return Encoding.UTF8.GetString(CanonicalJson.CanonicalBytes(outcome.Value));
    }
}
