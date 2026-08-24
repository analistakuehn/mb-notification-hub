namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal static partial class KillSwitchHoldReleaseServiceLogger
{
    [LoggerMessage(
        EventId = 7161,
        Level = LogLevel.Error,
        Message = "Falha ao liberar holds do kill switch; nenhum trabalho sem confirmação será retomado.")]
    internal static partial void KillSwitchReleaseFailed(this ILogger logger, Exception exception);
}
