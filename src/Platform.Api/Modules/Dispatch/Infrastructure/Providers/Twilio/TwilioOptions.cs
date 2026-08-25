using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

public sealed class TwilioOptions
{
    public const string SectionName = "Modules:Dispatch:Providers:Twilio";

    /// <summary>
    /// Destination format the adapter ships with: a Brazilian mobile number in
    /// E.164. It is the shape of the only market this phase sends to, and it is
    /// a default rather than a constant so opening a second market is a
    /// configuration change instead of a deploy of this assembly.
    /// </summary>
    internal const string DefaultDestinationPattern = @"^\+55\d{10,11}$";

    /// <summary>
    /// Longest validity this adapter asks the provider to hold a queued
    /// message for. Twilio revises its own ceiling on its own schedule, so the
    /// shipped value is the conservative bound and an operator raises it
    /// without waiting for a release.
    /// </summary>
    internal const int DefaultMaxValidityPeriodSeconds = 14_400;

    private static readonly string[] DefaultAllowedCountryPrefixes = ["+55"];

    private readonly Lazy<Regex> _destinationExpression;

    public TwilioOptions()
        => _destinationExpression = new Lazy<Regex>(() => new Regex(
            DestinationPattern,
            RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture,
            TimeSpan.FromSeconds(1)));

    [Required]
    [Url]
    public string BaseAddress { get; init; } = "https://api.twilio.com";

    /// <summary>Twilio product used for the configured SMS test account.</summary>
    public TwilioSmsProduct Product { get; init; } = TwilioSmsProduct.Verify;

    /// <summary>Credential family used to authenticate the provider call.</summary>
    public TwilioAuthenticationMode AuthenticationMode { get; init; } = TwilioAuthenticationMode.AuthToken;

    /// <summary>Twilio account identifier used by the route or Auth Token mode.</summary>
    public string AccountSid { get; init; } = "";

    /// <summary>API key SID used as the Basic authentication username in API key mode.</summary>
    public string ApiKeySid { get; init; } = "";

    /// <summary>Auth Token or API key secret supplied by the local secret store.</summary>
    public string CredentialSecret { get; init; } = "";

    /// <summary>Verified Twilio number used by Programmable Messaging.</summary>
    public string FromNumber { get; init; } = "";

    /// <summary>
    /// Messaging Service that owns the sender pool of this deployment. Set, it
    /// replaces the single sender number: the provider picks the sender, keeps
    /// the sticky sender per destination and applies the service's own
    /// compliance rules. Empty keeps the single-number path, so an environment
    /// with one verified test number still sends.
    /// </summary>
    public string MessagingServiceSid { get; init; } = "";

    /// <summary>
    /// Absolute public address of this hub's callback route for this provider,
    /// for example <c>https://hooks.example.com/webhooks/twilio</c>. The
    /// adapter appends the correlation identifiers of the attempt to it,
    /// because this provider echoes nothing back in the callback body and the
    /// address it was given is the only place left to carry them. Empty sends
    /// no callback address, which is the correct local behaviour: an
    /// unreachable address would only make the provider retry against nothing.
    /// </summary>
    public string StatusCallbackUrl { get; init; } = "";

    /// <summary>Twilio Verify service used when the product is Verify.</summary>
    public string ServiceSid { get; init; } = "";

    /// <summary>
    /// International prefixes the adapter accepts as destinations. Unset keeps
    /// the shipped list; a configured list replaces it whole, so a market can
    /// be opened or retired without a deploy. An explicitly empty list turns
    /// the prefix guard off and leaves only the format guard, in the same
    /// regime the callback origin allowlist follows.
    /// </summary>
    public string[]? AllowedCountryPrefixes { get; init; }

    /// <summary>
    /// Regular expression every destination must match before the adapter
    /// calls the provider. It is the cheapest guard against a malformed number
    /// consuming an SMS, and it is configuration because the shape of a valid
    /// number is a property of the market, not of this code. An unparsable
    /// pattern surfaces at send time, in the same regime as every other
    /// configuration guard of this module.
    /// </summary>
    [Required]
    public string DestinationPattern { get; init; } = DefaultDestinationPattern;

    /// <summary>
    /// Ceiling, in seconds, of the validity period asked of the provider. A
    /// remaining validity longer than this is sent as this value, because the
    /// provider refuses anything above its own limit and refusing the send
    /// here would cost a notification that is still perfectly deliverable.
    /// </summary>
    [Range(1, 86_400)]
    public int MaxValidityPeriodSeconds { get; init; } = DefaultMaxValidityPeriodSeconds;

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Maximum simultaneous SMS sends against Twilio from this host.</summary>
    [Range(1, 1_000)]
    public int MaxConcurrency { get; init; } = 4;

    public ProviderCircuitBreakerOptions CircuitBreaker { get; init; } = new();

    internal IReadOnlyList<string> EffectiveAllowedCountryPrefixes
        => AllowedCountryPrefixes ?? DefaultAllowedCountryPrefixes;

    internal Regex DestinationExpression => _destinationExpression.Value;
}

public enum TwilioSmsProduct
{
    ProgrammableMessaging = 1,
    Verify = 2,
}

public enum TwilioAuthenticationMode
{
    ApiKey = 1,
    AuthToken = 2,
}
