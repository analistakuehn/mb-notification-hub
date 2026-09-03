using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Port over one delivery provider. Each adapter translates one
/// <see cref="DispatchRequest"/> into one provider call and the provider's
/// answer into one normalized <see cref="ProviderResult"/>, nothing else. An
/// adapter knows no policy, no fallback, no attempt state and no audit;
/// timeout and circuit breaking wrap it from the outside, and redelivery of a
/// failed send belongs to the queue, because a provider send is not
/// idempotent and a blind retry can reach the same person twice.
/// </summary>
public interface IChannelProvider
{
    /// <summary>Channel this provider delivers.</summary>
    Channel Channel { get; }

    /// <summary>
    /// Stable provider identity (for example <c>sendgrid</c>, <c>fcm</c>):
    /// the key that provider configuration rows, circuit breakers and
    /// concurrency limits are scoped by, and the value recorded with every
    /// attempt.
    /// </summary>
    string ProviderKey { get; }

    /// <summary>
    /// Whether a send through this adapter composes the accepted set it is
    /// handed into the call it makes to the provider.
    /// <para>
    /// The adapter answers because the adapter is the only thing that knows:
    /// the answer is a property of the call it builds, and a table kept
    /// anywhere else would be a second statement about it, free to keep saying
    /// yes after the composition stopped carrying the members. It is a member
    /// of this contract and not a default, so an adapter added later cannot
    /// compile without deciding, and a decorator that forgot to forward it
    /// would answer for an adapter it is not.
    /// </para>
    /// <para>
    /// False is not a licence to drop the set. It is the statement a route
    /// reads to refuse the plan before the send exists, and this adapter is
    /// never handed a set it does not carry.
    /// </para>
    /// </summary>
    bool CarriesAttachments { get; }

    /// <summary>
    /// Performs one send. Every provider verdict, including permanent
    /// rejection, returns as a <see cref="ProviderResult"/>; exceptions are
    /// reserved for caller defects and misconfiguration.
    /// </summary>
    Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken);
}
