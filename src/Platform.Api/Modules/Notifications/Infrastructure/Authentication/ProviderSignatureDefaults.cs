using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;

/// <summary>
/// Names the provider-signature scheme publishes: the scheme itself, the
/// claim that carries the proven provider identity, the route value the
/// scheme reads the addressed provider from, and the request-scoped slot
/// holding the proven bytes.
/// </summary>
public static class ProviderSignatureDefaults
{
    /// <summary>Scheme name, as named by the policy that admits the webhook route.</summary>
    public const string SchemeName = "ProviderSignature";

    /// <summary>Claim carrying the identity the signature proved.</summary>
    public const string ProviderKeyClaimType = "provider_key";

    /// <summary>Route value naming the provider a callback is addressed to.</summary>
    public const string ProviderRouteValue = "provider";

    /// <summary>Configuration section of the scheme.</summary>
    public const string SectionName = "Modules:Notifications:ProviderWebhooks";

    /// <summary>Request-scoped slot where the scheme leaves the proven callback.</summary>
    internal const string VerifiedWebhookItemKey = "notifications.verified-provider-webhook";

    /// <summary>
    /// Reads the callback this request already proved. Absent means the proof
    /// never ran, which the endpoint treats as a defect rather than as
    /// unverified input to fall back on.
    /// </summary>
    internal static VerifiedProviderWebhook? FindVerifiedWebhook(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(VerifiedWebhookItemKey, out var stored)
            ? stored as VerifiedProviderWebhook
            : null;
    }
}
