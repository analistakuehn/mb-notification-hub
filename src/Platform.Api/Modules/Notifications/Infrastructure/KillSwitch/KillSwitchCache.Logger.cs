namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal static partial class KillSwitchCacheLogger
{
    [LoggerMessage(
        EventId = 7160,
        Level = LogLevel.Error,
        Message = "Falha ao atualizar o snapshot do kill switch; a avaliação permanece fechada.")]
    internal static partial void KillSwitchRefreshFailed(this ILogger logger, Exception exception);
}
