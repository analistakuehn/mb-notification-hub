using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Integration.V1;

/// <summary>
/// In-process lifecycle surface of the device tokens this module owns, for
/// the provider feedback path: when the push provider declares a token dead,
/// the dispatcher reports it here and this module stamps the invalidation
/// with its own audit trail and its own cache-invalidation event, exactly
/// like every other write of this context. The operation is idempotent: a
/// repeated report of an already invalidated token is a declarative no-op.
/// </summary>
public interface IDeviceTokenLifecycle
{
    /// <summary>
    /// Invalidates one device registration of the given recipient because the
    /// provider rejected its token permanently. <paramref name="reason"/>
    /// carries the provider's stable code for the trail. An unknown or
    /// foreign registration fails as not found; an already invalidated one
    /// succeeds without a second effect.
    /// </summary>
    Task<Result> InvalidateDeviceTokenAsync(
        string recipientId,
        Guid deviceTokenId,
        string reason,
        CancellationToken cancellationToken);
}
