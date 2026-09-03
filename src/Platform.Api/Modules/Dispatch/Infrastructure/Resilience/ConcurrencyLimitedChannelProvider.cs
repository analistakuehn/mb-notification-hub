using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

/// <summary>
/// Caps the simultaneous sends of one provider with a local semaphore, the
/// concurrency control this module owns (providers meter concurrency, and
/// HTTP/2 connection limits do not bound streams). Waiting honors the send's
/// cancellation token, so an expiring attempt stops queueing for a slot.
/// </summary>
internal sealed class ConcurrencyLimitedChannelProvider : IChannelProvider, IDisposable
{
    private readonly IChannelProvider _inner;
    private readonly SemaphoreSlim _slots;

    public ConcurrencyLimitedChannelProvider(IChannelProvider inner, int maxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);
        _inner = inner;
        _slots = new SemaphoreSlim(maxConcurrency, maxConcurrency);
    }

    public Channel Channel => _inner.Channel;

    public string ProviderKey => _inner.ProviderKey;

    /// <summary>
    /// The adapter's own answer, forwarded. Waiting for a slot changes nothing
    /// about what a message carries, and a decorator that answered for itself
    /// would say no for every adapter it wraps: every send with a set would
    /// then be refused by the route, on a deployment whose adapter carries it.
    /// </summary>
    public bool CarriesAttachments => _inner.CarriesAttachments;

    public async Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        await _slots.WaitAsync(cancellationToken);
        try
        {
            return await _inner.SendAsync(request, cancellationToken);
        }
        finally
        {
            _slots.Release();
        }
    }

    public void Dispose() => _slots.Dispose();
}
