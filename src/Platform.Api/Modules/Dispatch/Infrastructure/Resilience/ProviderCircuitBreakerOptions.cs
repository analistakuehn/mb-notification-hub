using System.ComponentModel.DataAnnotations;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

/// <summary>
/// Circuit-breaker knobs of one provider pipeline. Defaults follow the
/// accepted reliability posture: open at half the calls failing over a
/// thirty-second window. Tests lower the thresholds through configuration
/// instead of replaying dozens of failures.
/// </summary>
public sealed class ProviderCircuitBreakerOptions
{
    [Range(0.01, 1.0)]
    public double FailureRatio { get; init; } = 0.5;

    [Range(1, 600)]
    public int SamplingDurationSeconds { get; init; } = 30;

    [Range(2, 10_000)]
    public int MinimumThroughput { get; init; } = 10;

    [Range(1, 600)]
    public int BreakDurationSeconds { get; init; } = 15;
}
