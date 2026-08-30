using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>What the ceiling answers about one variables payload.</summary>
/// <remarks>
/// No member takes the zero value, so a caller that leaves the answer to a
/// default gets a value nothing acts on instead of silently getting the one
/// answer that admits the payload.
/// </remarks>
public enum VariablesPayloadVerdict
{
    /// <summary>Readable, and within the ceiling.</summary>
    Admitted = 1,

    /// <summary>
    /// The payload parses but does not transcode: an escape in it names no
    /// character. Nothing downstream can read it, so no number about it means
    /// anything, and it is refused for what it is rather than for its size.
    /// </summary>
    Unreadable = 2,

    /// <summary>Readable, and above the ceiling.</summary>
    AboveCeiling = 3,
}

/// <summary>
/// The ceiling on a variables payload, and the single door at which a payload
/// is both measured and read. Every door that hands a payload to the allowlist
/// scan and to the sandbox reads this rule, because a ceiling enforced at one
/// door bounds nothing: the same payload reaches the same walk and the same
/// render through the others, limited only by the transport's own body limit.
/// <para>
/// The two refusals travel together and are produced by one call, because they
/// are discovered by one traversal and separating them is what let half the
/// rule close. A payload that cannot be transcoded throws where it is walked,
/// and a size check written as a question about bytes alone answers that
/// payload by taking the caller down with it.
/// </para>
/// <para>
/// How the measure is defined, and why it is not the text as it arrived, lives
/// with the measure itself in <see cref="CompactJsonSize"/>. This type owns
/// only the number.
/// </para>
/// </summary>
public static class VariablesPayloadSize
{
    /// <summary>
    /// 256 kB. The allowlist scan walks every string value of the payload at
    /// any depth, twice per notification, at the ingestion gate and again at
    /// render, at a measured cost near 5 microseconds per kB of text; this
    /// ceiling is what keeps that walk under three milliseconds of CPU per
    /// notification instead of scaling with whatever the transport accepted.
    /// It is also the number the preview endpoint already published, so one
    /// value governs the three paths and nothing admitted at ingestion can be
    /// refused later for its size.
    /// </summary>
    public const int MaxBytes = 262_144;

    /// <summary>
    /// Assesses the payload in one traversal. An absent payload, and a JSON
    /// null, are always admitted: neither carries a value for the scan to walk
    /// and neither has anything to transcode.
    /// </summary>
    public static VariablesPayloadVerdict Assess(JsonElement? variables)
    {
        if (variables is not { } payload
            || payload.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return VariablesPayloadVerdict.Admitted;
        }

        CompactJsonSize.Outcome measured = CompactJsonSize.Measure(payload);
        if (!measured.IsReadable)
        {
            return VariablesPayloadVerdict.Unreadable;
        }

        return measured.ByteCount > MaxBytes
            ? VariablesPayloadVerdict.AboveCeiling
            : VariablesPayloadVerdict.Admitted;
    }
}
