using System.ComponentModel.DataAnnotations;
using System.Net;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// Verification and classification knobs for Twilio delivery callbacks.
/// Nothing here is required at host start: an environment without the SMS
/// channel boots with the section absent, and the missing secret surfaces as
/// a refusal at verification time, in the same regime as the sending adapter.
/// </summary>
public sealed class TwilioWebhookOptions : IValidatableObject
{
    /// <summary>Configuration section this options type binds to.</summary>
    public const string SectionName = "Modules:Dispatch:Webhooks:Twilio";

    private readonly Lazy<IPNetwork[]> _allowedNetworks;

    public TwilioWebhookOptions()
        => _allowedNetworks = new Lazy<IPNetwork[]>(
            () => WebhookRequestGuards.TryParseNetworks(AllowedNetworks, out IPNetwork[] parsed, out _)
                ? parsed
                : []);

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
    /// Networks allowed to deliver callbacks, in CIDR form, for example
    /// <c>54.172.60.0/24</c>. Empty, the shipped value, turns the allowlist
    /// off, and off is the posture this host is meant to run in: the address
    /// the application sees is the address of whatever proxy or load balancer
    /// terminates the connection, so pinning provider ranges belongs at that
    /// edge, where the client address is the real one. Filling this list on a
    /// host that sits behind a proxy refuses every authentic callback and
    /// raises a forgery alarm for each one.
    /// </summary>
    public string[] AllowedNetworks { get; init; } = [];

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

    /// <summary>The configured networks, parsed once and reused per callback.</summary>
    internal IReadOnlyList<IPNetwork> ParsedAllowedNetworks => _allowedNetworks.Value;

    /// <summary>
    /// Refuses a range nobody can parse at host start. The alternative is
    /// discovering it on the first real callback, where the failure is silent
    /// in the worst direction: an unparsed entry is an entry that never
    /// matches, so the guard would refuse authentic traffic and report it as
    /// forgery.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!WebhookRequestGuards.TryParseNetworks(AllowedNetworks, out _, out var invalid))
        {
            yield return new ValidationResult(
                $"'{invalid}' não é uma rede em notação CIDR.",
                [nameof(AllowedNetworks)]);
        }
    }
}
