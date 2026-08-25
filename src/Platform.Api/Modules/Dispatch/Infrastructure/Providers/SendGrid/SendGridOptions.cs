using System.ComponentModel.DataAnnotations;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

public sealed class SendGridOptions : IValidatableObject
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

    /// <summary>
    /// How far back the delivery lookup may search the message activity, in
    /// days. It is a commercial term of the contracted plan and not a property
    /// of this code: the shipped value is the reach of the plan without the
    /// paid activity add-on, and contracting the add-on raises it here without
    /// touching a line of the reconciliation. A message older than this is
    /// refused with its own code instead of being asked about, because an
    /// empty search reads exactly like a provider denying it ever saw the
    /// message.
    /// </summary>
    [Range(1, 90)]
    public int ActivityLookbackDays { get; init; } = 3;

    /// <summary>
    /// How many activity entries one search asks for. One attempt has few, and
    /// the whole set of them is the answer: the state machine takes the ones
    /// that move the attempt and ignores the rest.
    /// </summary>
    [Range(1, 100)]
    public int ActivityPageSize { get; init; } = 10;

    [Range(1, 120)]
    public int TimeoutSeconds { get; init; } = 5;

    /// <summary>Maximum simultaneous sends against SendGrid from this host.</summary>
    [Range(1, 1_000)]
    public int MaxConcurrency { get; init; } = 8;

    public ProviderCircuitBreakerOptions CircuitBreaker { get; init; } = new();

    /// <summary>
    /// Validates the nested circuit-breaker knobs, which the registration of
    /// this type does not reach on its own: their ranges would read as
    /// enforced and never be evaluated, letting an out-of-range threshold
    /// reach the pipeline at runtime.
    /// </summary>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => NestedOptionsValidation.Validate(CircuitBreaker, nameof(CircuitBreaker));
}
