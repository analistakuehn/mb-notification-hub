using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

public sealed class SendGridOptions
{
    public const string SectionName = "Modules:Dispatch:Providers:SendGrid";

    [Required]
    [Url]
    public string BaseAddress { get; init; } = "https://api.sendgrid.com";

    /// <summary>
    /// Provided by the secret store per environment; never committed. An
    /// empty key fails at send time with an explicit misconfiguration error,
    /// not at host start, so environments without the e-mail channel still
    /// boot.
    /// </summary>
    public string ApiKey { get; init; } = "";

    /// <summary>Verified sender of every message this host dispatches.</summary>
    public string SenderEmail { get; init; } = "";

    public string? SenderName { get; init; }

    /// <summary>
    /// Sandbox mode validates the request on SendGrid without delivering.
    /// Enabled by default on purpose: real delivery requires an explicit
    /// production override.
    /// </summary>
    public bool SandboxMode { get; init; } = true;

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Maximum simultaneous sends against SendGrid from this host.</summary>
    [Range(1, 1_000)]
    public int MaxConcurrency { get; init; } = 8;

    public ProviderCircuitBreakerOptions CircuitBreaker { get; init; } = new();
}
