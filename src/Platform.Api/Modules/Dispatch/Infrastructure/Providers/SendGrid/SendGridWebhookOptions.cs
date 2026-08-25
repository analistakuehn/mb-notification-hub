using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Verification and classification knobs for SendGrid event callbacks.
/// Nothing here is required at host start: an environment without the e-mail
/// channel boots with the section absent, and the missing key surfaces as a
/// refusal at verification time, in the same regime as the sending adapter.
/// </summary>
public sealed class SendGridWebhookOptions
{
    /// <summary>Configuration section this options type binds to.</summary>
    public const string SectionName = "Modules:Dispatch:Webhooks:SendGrid";

    private static readonly string[] DefaultHardBounceCodes =
    [
        "bounce",
        "Bounced Address",
        "Unsubscribed Address",
        "Spam Report",
    ];

    private static readonly string[] DefaultInvalidDestinationCodes =
    [
        "Invalid",
        "Invalid SMTP",
    ];

    private static readonly string[] DefaultUntrackedEvents =
    [
        "click",
        "unsubscribe",
        "group_unsubscribe",
        "group_resubscribe",
        "spamreport",
    ];

    /// <summary>
    /// Event webhook verification key, Base64 of the DER public key the
    /// provider publishes when signing is enabled. Supplied by the secret
    /// store per environment. Empty means this host cannot verify SendGrid
    /// callbacks and refuses every one of them.
    /// </summary>
    public string PublicKey { get; init; } = "";

    /// <summary>
    /// Textual address prefixes allowed to deliver callbacks. Empty, the
    /// shipped value, turns the allowlist off, because pinning provider
    /// ranges belongs first to the network edge and a half-filled list here
    /// would drop authentic callbacks in silence.
    /// </summary>
    public string[] AllowedIpPrefixes { get; init; } = [];

    /// <summary>
    /// Half-width of the replay window. Unlike the SMS callback, this one
    /// always carries a timestamp and the timestamp is part of the signed
    /// payload, so the window is mandatory: without it a captured callback
    /// stays valid forever and can be replayed to walk an attempt backwards.
    /// </summary>
    [Range(1, 86_400)]
    public int TimestampWindowSeconds { get; init; } = 600;

    /// <summary>
    /// Provider reasons and bounce types that mean the destination refuses
    /// mail permanently. Unset keeps the shipped list; a configured list
    /// replaces it whole, so an operator can retire a term the day the
    /// provider changes its meaning.
    /// </summary>
    public string[]? HardBounceCodes { get; init; }

    /// <summary>
    /// Provider reasons that mean the address does not exist. Unset keeps the
    /// shipped list; a configured list replaces it whole.
    /// </summary>
    public string[]? InvalidDestinationCodes { get; init; }

    /// <summary>
    /// Event names this hub deliberately does not track. A batch mixes
    /// delivery events with engagement events, so these are dropped quietly
    /// while any other unmapped word is reported: that is how a new delivery
    /// word the provider introduces becomes visible instead of vanishing.
    /// </summary>
    public string[]? UntrackedEvents { get; init; }

    internal IReadOnlyList<string> EffectiveHardBounceCodes
        => HardBounceCodes ?? DefaultHardBounceCodes;

    internal IReadOnlyList<string> EffectiveInvalidDestinationCodes
        => InvalidDestinationCodes ?? DefaultInvalidDestinationCodes;

    internal IReadOnlyList<string> EffectiveUntrackedEvents
        => UntrackedEvents ?? DefaultUntrackedEvents;
}
