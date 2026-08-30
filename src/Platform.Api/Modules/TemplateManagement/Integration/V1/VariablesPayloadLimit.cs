using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// What the published admission rule answers about one variables payload. It
/// mirrors the domain verdict instead of exposing it, because the published
/// surface may not hand a consumer a type from this module's domain: a
/// consumer that named one would depend on it, and the domain would stop being
/// free to rename what only it owns.
/// </summary>
/// <remarks>
/// No member takes the zero value, so a consumer that leaves the answer to a
/// default gets a value nothing acts on instead of silently getting the one
/// answer that admits the payload.
/// </remarks>
public enum VariablesPayloadAdmission
{
    /// <summary>Readable, and within the ceiling.</summary>
    Admitted = 1,

    /// <summary>
    /// The payload parses but does not transcode: an escape in it names no
    /// character. Nothing that renders it can read it, so a consumer refuses
    /// it at its own door rather than carrying it to a render that throws.
    /// </summary>
    Unreadable = 2,

    /// <summary>Readable, and above the ceiling.</summary>
    AboveCeiling = 3,
}

/// <summary>
/// The admission rule for the variables payload this module renders, published
/// so a consumer can refuse a payload at its own door instead of carrying it
/// all the way to the render. This module owns the rule because it owns what
/// the rule protects: the allowlist scan over every string value and the
/// sandbox that turns the payload into script objects. A second rule chosen by
/// a consumer would drift from this one, and a consumer whose ceiling sat
/// above it would admit a request that this module then refuses, which is a
/// rejection in the pipeline instead of an answer at the door.
/// </summary>
public static class VariablesPayloadLimit
{
    /// <summary>Bytes of compact UTF-8 JSON a variables payload may occupy.</summary>
    public static int MaxBytes => VariablesPayloadSize.MaxBytes;

    /// <summary>
    /// Assesses the payload the same way the render assesses it, in one
    /// traversal, so the two can never disagree about one payload and no
    /// consumer can close half the rule.
    /// </summary>
    public static VariablesPayloadAdmission Assess(JsonElement? variables)
    {
        VariablesPayloadVerdict verdict = VariablesPayloadSize.Assess(variables);
        return verdict switch
        {
            VariablesPayloadVerdict.Admitted => VariablesPayloadAdmission.Admitted,
            VariablesPayloadVerdict.Unreadable => VariablesPayloadAdmission.Unreadable,
            VariablesPayloadVerdict.AboveCeiling => VariablesPayloadAdmission.AboveCeiling,
            _ => throw new InvalidOperationException(
                $"Unsupported variables payload verdict '{verdict}'."),
        };
    }
}
