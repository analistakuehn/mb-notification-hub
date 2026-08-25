using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;

/// <summary>
/// Joins a provider key with the delivery lookup this process hosts for it.
/// <para>
/// An unmatched key is an ordinary answer here, and that is the whole design:
/// a provider whose platform offers no lookup after the send simply registers
/// nothing, and this resolver refuses. The refusal is the record that the
/// attempt cannot be settled by asking, which is a different fact from a
/// provider that was asked and answered nothing, and the caller writes the two
/// down differently.
/// </para>
/// </summary>
internal sealed class ProviderDeliveryLookupResolver(
    IEnumerable<IProviderDeliveryLookup> lookups) : IProviderDeliveryLookupResolver
{
    public Result<IProviderDeliveryLookup> Resolve(string providerKey)
    {
        if (string.IsNullOrWhiteSpace(providerKey))
        {
            return ProviderLookupRefusal.Refuse<IProviderDeliveryLookup>(
                ProviderLookupRefusal.ProviderUnknown);
        }

        IProviderDeliveryLookup? match = lookups.FirstOrDefault(candidate =>
            string.Equals(candidate.ProviderKey, providerKey, StringComparison.Ordinal));

        return match is null
            ? ProviderLookupRefusal.Refuse<IProviderDeliveryLookup>(
                ProviderLookupRefusal.LookupUnsupported)
            : Result.Success(match);
    }
}
