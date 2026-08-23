using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Integration.V1;

/// <summary>
/// In-process read surface of this module for an evidence composer. It is
/// separate from the support query surface on purpose: this one projects the
/// per-rule policy evidence and opens the stored render, which the support
/// surface must never do, and every call site of it is a disclosure.
/// </summary>
public interface INotificationEvidence
{
    /// <summary>
    /// Everything this module records about one notification. An unknown
    /// identity fails as not found.
    /// </summary>
    Task<Result<NotificationEvidence>> FindAsync(Guid notificationId, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the stored content of one attempt and returns its masked form with
    /// the masked hash recomputed. An unknown notification or sequence fails as
    /// not found.
    /// </summary>
    Task<Result<RevealedAttemptContent>> RevealAttemptContentAsync(
        Guid notificationId,
        int sequence,
        CancellationToken cancellationToken);
}
