using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Dispatch.Integration.V1;

/// <summary>
/// Where one delivery attempt goes, discriminated by channel. The destination
/// travels beside the rendered content, never inside it: content is hashed
/// and audited as-is, while the target is per-attempt personal data resolved
/// by the caller. The hierarchy is closed inside this module.
/// </summary>
public abstract record DeliveryTarget
{
    private protected DeliveryTarget()
    {
    }
}

/// <summary>Destination of an e-mail attempt.</summary>
public sealed record EmailDeliveryTarget(
    [property: PersonalData] string EmailAddress) : DeliveryTarget;

/// <summary>Destination of a push attempt: one device token, one attempt.</summary>
public sealed record PushDeliveryTarget(
    [property: PersonalData] string DeviceToken) : DeliveryTarget;
