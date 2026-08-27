using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// Masks the values of sensitive variables in a variables payload before the
/// masked render: scalar values become the fixed mask, containers keep their
/// shape with every leaf masked, and null stays null so optional-variable
/// behavior is preserved. The mask is irreversible on purpose: the stored form
/// proves that a value was sent, never which one. Which nodes a sensitive name
/// addresses is decided by the shared structural rule, so this form and the
/// stored projection of the same request always mask the same values.
/// </summary>
public static class VariableMasking
{
    public const string MaskedValue = SensitiveValueMask.MaskedValue;

    /// <summary>
    /// The masked payload plus the two facts the caller cannot recover from it:
    /// whether anything changed, and whether a sensitive name failed to address
    /// the shape of the payload.
    /// </summary>
    public static SensitiveValueMask.Outcome Mask(
        JsonElement? variables,
        IReadOnlyList<string> sensitiveVariables)
        => variables is { } payload
            ? SensitiveValueMask.Apply(payload, sensitiveVariables)
            : default;

    /// <summary>True when the payload carries at least one sensitive value to mask.</summary>
    public static bool RequiresMasking(JsonElement? variables, IReadOnlyList<string> sensitiveVariables)
        => Mask(variables, sensitiveVariables).Changed;

    /// <summary>
    /// Returns the payload with every sensitive variable masked; a payload
    /// without sensitive values comes back unchanged.
    /// </summary>
    public static JsonElement? MaskSensitiveVariables(
        JsonElement? variables,
        IReadOnlyList<string> sensitiveVariables)
    {
        SensitiveValueMask.Outcome outcome = Mask(variables, sensitiveVariables);
        return outcome.Changed ? outcome.Value : variables;
    }
}
