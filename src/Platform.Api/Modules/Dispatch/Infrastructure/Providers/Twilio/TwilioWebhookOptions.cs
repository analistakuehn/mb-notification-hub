using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// Verification and classification knobs for Twilio delivery callbacks.
/// Nothing here is required at host start: an environment without the SMS
/// channel boots with the section absent, and the missing secret surfaces as
/// a refusal at verification time, in the same regime as the sending adapter.
/// </summary>
public sealed class TwilioWebhookOptions
{
    /// <summary>Configuration section this options type binds to.</summary>
    public const string SectionName = "Modules:Dispatch:Webhooks:Twilio";

    private static readonly string[] DefaultHardBounceCodes = ["21610"];

    private static readonly string[] DefaultInvalidDestinationCodes =
        ["21211", "21614", "30003", "30005", "30006"];

    /// <summary>
    /// Account auth token, the key of the callback signature. Supplied by the
    /// secret store per environment and never committed. Empty means this host
    /// cannot verify Twilio callbacks and refuses every one of them.
    /// </summary>
    public string AuthToken { get; init; } = "";

    /// <summary>
    /// Textual address prefixes allowed to deliver callbacks, for example
    /// <c>54.172.60.</c>. Empty, the shipped value, turns the allowlist off,
    /// because pinning provider ranges belongs first to the network edge and a
    /// half-filled list here would drop authentic callbacks in silence.
    /// </summary>
    public string[] AllowedIpPrefixes { get; init; } = [];

    /// <summary>Half-width of the replay window applied when the callback carries a timestamp.</summary>
    [Range(1, 86_400)]
    public int TimestampWindowSeconds { get; init; } = 300;

    /// <summary>
    /// Form field carrying the callback timestamp. The message status
    /// callback does not send one, and the window is skipped when the field is
    /// absent: refusing every callback for a field the provider never sends
    /// would reject all delivery feedback. Replay protection for this provider
    /// therefore rests on deduplication by event identifier downstream, and the
    /// window engages as soon as a Twilio product does send the field.
    /// </summary>
    public string TimestampParameterName { get; init; } = "Timestamp";

    /// <summary>
    /// Provider error codes that mean the destination refuses this kind of
    /// message permanently. Unset keeps the shipped list, which names the
    /// opt-out code; a configured list replaces it whole, so an operator can
    /// retire a code the day Twilio changes its meaning.
    /// </summary>
    public string[]? HardBounceCodes { get; init; }

    /// <summary>
    /// Provider error codes that mean the number does not exist or cannot
    /// receive messages: a malformed destination, a number that is not mobile,
    /// an unknown or unreachable handset, and a landline or unreachable
    /// carrier. Unset keeps the shipped list; a configured list replaces it
    /// whole.
    /// <para>
    /// The list names codes an operator could read as temporary, and that is
    /// deliberate: on this channel a signal alone suppresses nothing, because
    /// the ledger asks for two refusals inside a week. A handset that is
    /// unreachable once is an outage and produces no suppression; the same
    /// number refusing twice in seven days is the destination, not the day.
    /// </para>
    /// </summary>
    public string[]? InvalidDestinationCodes { get; init; }

    internal IReadOnlyList<string> EffectiveHardBounceCodes
        => HardBounceCodes ?? DefaultHardBounceCodes;

    internal IReadOnlyList<string> EffectiveInvalidDestinationCodes
        => InvalidDestinationCodes ?? DefaultInvalidDestinationCodes;
}
