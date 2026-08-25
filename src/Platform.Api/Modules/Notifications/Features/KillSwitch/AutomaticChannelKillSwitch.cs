using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

/// <summary>
/// Turns a provider circuit that stays open into the channel kill switch,
/// under the gate described by <see cref="AutomaticChannelKillSwitchOptions"/>
/// and through exactly the same transition a human operator performs: same
/// row, same audit action, same cache invalidation, only the actor differs.
/// That symmetry is the point, because the way out of this stop is a person
/// reactivating the channel and they must find a state they recognize.
/// <para>
/// Only the open circuit feeds the window. An attempt that ended because the
/// notification ran out of validity never reaches a provider and says nothing
/// about the provider's health; counting it would stop the channel for the
/// customer's own deadline instead of for a degraded provider, and it would do
/// so exactly when a queue is running late, which is when a channel is needed
/// most.
/// </para>
/// </summary>
internal sealed class AutomaticChannelKillSwitch(
    ChannelCircuitObserver observer,
    IOptions<AutomaticChannelKillSwitchOptions> options,
    KillSwitchAdministration.Handler administration,
    ILogger<AutomaticChannelKillSwitch> logger)
{
    /// <summary>Stable reason recorded on the trail of an automatic stop.</summary>
    internal const string StopReason = "provider-circuit-open";

    /// <summary>Feeds one send verdict into the observation of this channel.</summary>
    internal async Task ObserveAsync(
        string channel,
        ChannelCircuitSignal signal,
        CancellationToken cancellationToken)
    {
        AutomaticChannelKillSwitchOptions config = options.Value;
        if (!config.Enabled) return;

        switch (signal)
        {
            case ChannelCircuitSignal.ProviderAnswered:
                observer.ObserveProviderAnswered(channel);
                return;
            case ChannelCircuitSignal.CircuitOpen:
                if (observer.ObserveOpenCircuit(channel, config.OpenCircuitWindow))
                {
                    await StopChannelAsync(channel, config.OpenCircuitWindow, cancellationToken);
                }

                return;
            default:
                return;
        }
    }

    /// <summary>
    /// Best effort by design: the attempt this observation came from is already
    /// settled, so a redelivery would resolve as a duplicate and could not
    /// retry the stop. A failure here leaves the channel running and says so
    /// under its own alarm, which is the safe direction: the operator still has
    /// the manual switch, and a hub that stopped sending because it failed to
    /// record why would be worse than one that kept going.
    /// </summary>
    private async Task StopChannelAsync(
        string channel,
        TimeSpan window,
        CancellationToken cancellationToken)
    {
        try
        {
            Result<KillSwitchAdministration.ChangeResult> changed = await administration.HandleAsync(
                new KillSwitchAdministration.ChangeCommand(
                    KillSwitchScope.Channel,
                    channel,
                    Active: true,
                    DispatchingAuditVocabulary.ActorIdDispatcher,
                    AuditActorTypes.System,
                    StopReason),
                cancellationToken);
            if (changed.IsFailure)
            {
                logger.AutomaticChannelStopFailed(channel, changed.Error ?? StopReason);
                return;
            }

            if (changed.Value!.Changed)
            {
                logger.AutomaticChannelStopped(channel, (int)window.TotalMinutes);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.AutomaticChannelStopThrew(channel, exception);
        }
    }
}
