using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

/// <summary>
/// Spends one token of the provider's contracted rate before the send reaches
/// the adapter. It wraps the concurrency limiter rather than the other way
/// round: a send with no budget must not first wait for a slot it is going to
/// give back. A refusal is a throttle, never a rejection: this hub decided not
/// to call, the provider said nothing, and the message is still perfectly
/// deliverable a moment later.
/// </summary>
internal sealed class RateLimitedChannelProvider(
    IChannelProvider inner,
    IProviderRateBudget budget) : IChannelProvider, IDisposable
{
    /// <summary>
    /// Stable code of a send this hub held back. It is not a provider code on
    /// purpose: the caller settles it like a throttle but must be able to tell
    /// congestion of our own making from congestion the provider reported.
    /// </summary>
    internal const string RateLimitedErrorCode = "rate-limited";

    public Channel Channel => inner.Channel;

    public string ProviderKey => inner.ProviderKey;

    /// <summary>
    /// The adapter's own answer, forwarded. Spending a token changes nothing
    /// about what a message carries, and a decorator that answered for itself
    /// would say no for every adapter it wraps: every send with a set would
    /// then be refused by the route, on a deployment whose adapter carries it.
    /// </summary>
    public bool CarriesAttachments => inner.CarriesAttachments;

    public async Task<ProviderResult> SendAsync(
        DispatchRequest request,
        CancellationToken cancellationToken)
    {
        ProviderRateDecision decision = await budget.TryConsumeAsync(ProviderKey, cancellationToken);
        return decision.Allowed
            ? await inner.SendAsync(request, cancellationToken)
            : ProviderResult.Throttled(RateLimitedErrorCode, null, decision.RetryAfter);
    }

    /// <summary>
    /// Disposes the wrapped provider: the container tracks the instance this
    /// decorator was registered as, and never the one it was handed.
    /// </summary>
    public void Dispose() => (inner as IDisposable)?.Dispose();
}
