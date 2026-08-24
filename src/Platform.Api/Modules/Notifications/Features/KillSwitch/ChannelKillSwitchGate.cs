using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

internal sealed class ChannelKillSwitchGate(
    IKillSwitch killSwitch,
    KillSwitchHoldWriter holdWriter,
    AttemptDispatchWriter dispatchWriter,
    IChannelProviderResolver providerResolver)
{
    internal const string UnavailableReason = "channel-kill-switch-unavailable";

    internal async Task<MessageDisposition?> EvaluateAsync(
        Notification notification,
        NotificationAttempt attempt,
        MessageEnvelope envelope,
        bool claimed,
        CancellationToken cancellationToken)
    {
        MessageDisposition? stopped = await EvaluateScopeAsync(
            KillSwitchScope.Application,
            notification.Application,
            notification,
            attempt,
            envelope,
            claimed,
            cancellationToken);
        if (stopped is not null)
        {
            return stopped;
        }

        return await EvaluateScopeAsync(
            KillSwitchScope.Channel,
            attempt.Channel,
            notification,
            attempt,
            envelope,
            claimed,
            cancellationToken);
    }

    private async Task<MessageDisposition?> EvaluateScopeAsync(
        KillSwitchScope scope,
        string key,
        Notification notification,
        NotificationAttempt attempt,
        MessageEnvelope envelope,
        bool claimed,
        CancellationToken cancellationToken)
    {
        KillSwitchEvaluation evaluation = await killSwitch.EvaluateAsync(
            scope, key, cancellationToken);
        switch (evaluation)
        {
            case KillSwitchEvaluation.Allowed:
                return null;
            case KillSwitchEvaluation.Blocked:
                KillSwitchHoldRequest hold = KillSwitchHoldWriter.Dispatch(
                    notification, attempt, envelope) with
                {
                    Scope = scope,
                    Key = key,
                };
                await holdWriter.HoldAsync(
                    hold,
                    claimed ? attempt.Id : null,
                    cancellationToken);
                return new MessageDisposition.Processed();
            case KillSwitchEvaluation.Unavailable:
                if (claimed)
                {
                    await dispatchWriter.RevertToQueuedAsync(attempt, cancellationToken);
                }

                var unavailableReason = scope == KillSwitchScope.Application
                    ? ApplicationKillSwitchGate.UnavailableReason
                    : UnavailableReason;
                return new MessageDisposition.Postponed(Delay: null, unavailableReason);
            default:
                throw new InvalidOperationException(
                    $"Avaliação de kill switch desconhecida: {evaluation}.");
        }
    }

    internal Task<Result<IChannelProvider>> ResolveProviderAsync(
        Channel channel,
        CancellationToken cancellationToken)
        => providerResolver.ResolveAsync(channel, cancellationToken);
}
