using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Webhooks;

/// <summary>
/// Joins a provider key taken from an inbound request with the interpreter
/// this process hosts for it. Unlike the send-side resolution, the key here is
/// untrusted input rather than configuration, so an unmatched key is ordinary
/// traffic and comes back as a refusal instead of an integration failure.
/// </summary>
internal sealed class ProviderWebhookInterpreterResolver(
    IEnumerable<IProviderWebhookInterpreter> interpreters) : IProviderWebhookInterpreterResolver
{
    public Result<IProviderWebhookInterpreter> Resolve(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return ProviderWebhookRefusal.Refuse<IProviderWebhookInterpreter>(
                ProviderWebhookRefusal.ProviderUnknown);
        }

        IProviderWebhookInterpreter? match = interpreters.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderKey, providerKey, StringComparison.Ordinal));

        return match is null
            ? ProviderWebhookRefusal.Refuse<IProviderWebhookInterpreter>(
                ProviderWebhookRefusal.ProviderUnknown)
            : Result.Success(match);
    }
}
