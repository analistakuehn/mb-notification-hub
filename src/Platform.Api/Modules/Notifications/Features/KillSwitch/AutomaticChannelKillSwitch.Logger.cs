namespace NotificationHub.Api.Modules.Notifications.Features.KillSwitch;

internal static partial class AutomaticChannelKillSwitchLogger
{
    [LoggerMessage(EventId = 7220, Level = LogLevel.Critical, Message = "Alarme: o canal {Channel} foi parado automaticamente após o circuito do provedor permanecer aberto por mais de {WindowMinutes} minutos. A reativação é humana, pela administração do kill switch.")]
    internal static partial void AutomaticChannelStopped(this ILogger logger, string channel, int windowMinutes);

    [LoggerMessage(EventId = 7221, Level = LogLevel.Error, Message = "Alarme: a parada automática do canal {Channel} não foi registrada ({Error}); o canal segue enviando e a parada manual continua disponível.")]
    internal static partial void AutomaticChannelStopFailed(this ILogger logger, string channel, string error);

    [LoggerMessage(EventId = 7222, Level = LogLevel.Error, Message = "Alarme: a parada automática do canal {Channel} lançou exceção; o canal segue enviando e a parada manual continua disponível.")]
    internal static partial void AutomaticChannelStopThrew(this ILogger logger, string channel, Exception exception);
}
