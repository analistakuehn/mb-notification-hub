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
    /// Performs one send. Every provider verdict, including permanent
    /// rejection, returns as a <see cref="ProviderResult"/>; exceptions are
    /// reserved for caller defects and misconfiguration.
    /// </summary>
    Task<ProviderResult> SendAsync(DispatchRequest request, CancellationToken cancellationToken);
}
