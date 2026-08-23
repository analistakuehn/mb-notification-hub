using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

public sealed class FcmOptions
{
    public const string SectionName = "Modules:Dispatch:Providers:Fcm";

    [Required]
    [Url]
    public string BaseAddress { get; init; } = "https://fcm.googleapis.com";

    /// <summary>Firebase project that owns the device tokens.</summary>
    public string ProjectId { get; init; } = "";

    /// <summary>
    /// Service-account identity used for the OAuth JWT-bearer grant. The
    /// private key arrives from the secret store per environment; an empty
    /// value fails at send time with an explicit misconfiguration error, not
    /// at host start, so environments without the push channel still boot.
    /// </summary>
    public string ServiceAccountEmail { get; init; } = "";

    public string ServiceAccountPrivateKeyPem { get; init; } = "";

    [Required]
    [Url]
    public string TokenUri { get; init; } = "https://oauth2.googleapis.com/token";

    public string TokenScope { get; init; } = "https://www.googleapis.com/auth/firebase.messaging";

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Maximum simultaneous sends against FCM from this host.</summary>
    [Range(1, 1_000)]
    public int MaxConcurrency { get; init; } = 8;

    public ProviderCircuitBreakerOptions CircuitBreaker { get; init; } = new();
}
