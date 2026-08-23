using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;

/// <summary>
/// Second stage: recipient id becomes contact points. PII is born here, in
/// memory only. Critical and authentication flows tolerate the last known
/// snapshot when the directory's local read degrades; every other class lets
/// the failure propagate so the message returns to the queue with backoff,
/// never turning degradation into a deferral.
/// </summary>
internal sealed class ResolveStage(IRecipientDirectory recipientDirectory) : INotificationStage
{
    internal const string AuthenticationPurpose = "authentication";
    internal const string ReasonNoValidContact = "no-valid-contact";

    public string Name => "Resolve";

    public async Task<StageOutcome> ExecuteAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        RecipientReadFallback fallback = IsCriticalOrAuthentication(context)
            ? RecipientReadFallback.LastKnown
            : RecipientReadFallback.None;
        Result<RecipientSnapshot> snapshot = await recipientDirectory.FindAsync(
            context.Notification.RecipientId, fallback, cancellationToken);
        if (snapshot.IsFailure)
        {
            // An unknown recipient is a business outcome, not an error: the
            // anti-enumeration contract accepted the request anyway.
            context.LastReason = ReasonNoValidContact;
            return StageOutcome.Reject;
        }

        context.Recipient = snapshot.Value;
        return StageOutcome.Continue;
    }

    internal static bool IsCriticalOrAuthentication(NotificationContext context)
        => context.Notification.Class == NotificationClasses.Critical
            || string.Equals(context.Template?.Purpose, AuthenticationPurpose, StringComparison.Ordinal);
}
