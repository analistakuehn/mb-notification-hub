using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

internal sealed class ApplicationKillSwitchGate(
    IKillSwitch killSwitch,
    KillSwitchHoldWriter holdWriter)
{
    internal const string UnavailableReason = "application-kill-switch-unavailable";

    internal async Task<MessageDisposition?> EvaluateAsync(
        Notification notification,
        MessageEnvelope envelope,
        string workKind,
        CancellationToken cancellationToken)
    {
        KillSwitchEvaluation evaluation = await killSwitch.EvaluateAsync(
            KillSwitchScope.Application,
            notification.Application,
            cancellationToken);
        switch (evaluation)
        {
            case KillSwitchEvaluation.Allowed:
                return null;
            case KillSwitchEvaluation.Blocked:
                await holdWriter.HoldAsync(
                    KillSwitchHoldWriter.Core(
                        notification,
                        envelope,
                        workKind,
                        KillSwitchScope.Application,
                        notification.Application),
                    claimedAttemptId: null,
                    cancellationToken);
                return new MessageDisposition.Processed();
            case KillSwitchEvaluation.Unavailable:
                return new MessageDisposition.Postponed(Delay: null, UnavailableReason);
            default:
                throw new InvalidOperationException(
                    $"Avaliação de kill switch desconhecida: {evaluation}.");
        }
    }
}
