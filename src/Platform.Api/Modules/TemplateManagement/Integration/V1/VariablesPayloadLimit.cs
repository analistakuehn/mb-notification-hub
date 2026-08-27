using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// The ceiling on the variables payload this module renders, published so a
/// consumer can refuse an oversized payload at its own door instead of
/// carrying it all the way to the render. This module owns the number because
/// it owns the cost the number bounds: the allowlist scan over every string
/// value and the sandbox that turns the payload into script objects. A second
/// number chosen by a consumer would drift from this one, and a consumer whose
/// ceiling sat above it would admit a request that this module then refuses,
/// which is a rejection in the pipeline instead of an answer at the door.
/// </summary>
public static class VariablesPayloadLimit
{
    /// <summary>Bytes of compact UTF-8 JSON a variables payload may occupy.</summary>
    public static int MaxBytes => VariablesPayloadSize.MaxBytes;

    /// <summary>
    /// Whether the payload is above the ceiling, measured the same way the
    /// render measures it, so the two can never disagree about one payload.
    /// </summary>
    public static bool Exceeds(JsonElement? variables) => VariablesPayloadSize.ExceedsMaxBytes(variables);
}
