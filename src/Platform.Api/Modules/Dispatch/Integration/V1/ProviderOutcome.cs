namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Normalized verdict of one provider call. Adapters translate every
/// provider-specific response into exactly one of these values so retry,
/// fallback and token-invalidation decisions never read provider details.
/// </summary>
public enum ProviderOutcome
{
    /// <summary>The provider took responsibility for the message.</summary>
    Accepted = 0,

    /// <summary>
    /// Permanent rejection: the same request will never succeed (invalid
    /// destination, unregistered device token, rejected payload). The caller
    /// must not retry this attempt on this provider.
    /// </summary>
    Rejected = 1,

    /// <summary>
    /// The provider refused for rate or quota reasons. Transient by nature;
    /// <see cref="ProviderResult.RetryAfter"/> carries the wait the provider
    /// asked for, when it named one.
    /// </summary>
    Throttled = 2,

    /// <summary>
    /// Transient failure without a provider verdict: 5xx, timeout, network
    /// fault or open circuit. Whether the message reached the provider is
    /// unknown; redelivery is the queue's decision, never the adapter's.
    /// </summary>
    TransientError = 3,
}
