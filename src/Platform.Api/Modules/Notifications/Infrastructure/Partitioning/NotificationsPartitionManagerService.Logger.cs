namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Partitioning;

internal static partial class NotificationsPartitionManagerServiceLogger
{
    [LoggerMessage(EventId = 7040, Level = LogLevel.Information, Message = "Gerenciador de partições de notificações desabilitado por configuração.")]
    internal static partial void NotificationPartitionManagerDisabled(this ILogger logger);

    [LoggerMessage(EventId = 7041, Level = LogLevel.Information, Message = "Gerenciador de partições de notificações iniciado com intervalo {Interval}.")]
    internal static partial void NotificationPartitionManagerStarted(this ILogger logger, TimeSpan interval);

    [LoggerMessage(EventId = 7042, Level = LogLevel.Error, Message = "Falha em uma rodada de provisionamento de partições de notificações; nova tentativa no próximo ciclo.")]
    internal static partial void NotificationPartitionRoundFailed(this ILogger logger, Exception exception);
}
