using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

/// <summary>The fail-closed emergency-stop contract shared by every ingress and worker adapter.</summary>
public interface IKillSwitch
{
    ValueTask<KillSwitchEvaluation> EvaluateAsync(
        KillSwitchScope scope,
        string key,
        CancellationToken cancellationToken);
}

public enum KillSwitchEvaluation
{
    Allowed = 0,
    Blocked = 1,
    Unavailable = 2,
}
