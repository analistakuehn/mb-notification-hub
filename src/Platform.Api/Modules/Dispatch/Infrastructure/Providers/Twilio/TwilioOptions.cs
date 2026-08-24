using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

public sealed class TwilioOptions
{
    public const string SectionName = "Modules:Dispatch:Providers:Twilio";

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

    /// <summary>Twilio Verify service used when the product is Verify.</summary>
    public string ServiceSid { get; init; } = "";

    /// <summary>Allowed international prefixes for the local SMS test.</summary>
    public string[] AllowedCountryPrefixes { get; init; } = ["+55"];

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Maximum simultaneous SMS sends against Twilio from this host.</summary>
    [Range(1, 1_000)]
    public int MaxConcurrency { get; init; } = 4;

    public ProviderCircuitBreakerOptions CircuitBreaker { get; init; } = new();
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
