namespace NotificationHub.Api.Modules.Notifications.Features.Fallback;

internal static partial class FallbackRequestHandlerLogger
{
    [LoggerMessage(EventId = 7140, Level = LogLevel.Information, Message = "Fallback da notificação {NotificationId} enfileirou o attempt {AttemptId} no canal {Channel}.")]
    internal static partial void FallbackAttemptQueued(this ILogger logger, Guid notificationId, Guid attemptId, string channel);

    [LoggerMessage(EventId = 7141, Level = LogLevel.Warning, Message = "Fallback encerrou a notificação {NotificationId} em '{Status}' ({Reason}).")]
    internal static partial void FallbackEndedNotification(this ILogger logger, Guid notificationId, string status, string reason);

    [LoggerMessage(EventId = 7142, Level = LogLevel.Information, Message = "Gatilho de fallback ignorado: a notificação {NotificationId} está em '{Status}'.")]
    internal static partial void FallbackDuplicateSkipped(this ILogger logger, Guid notificationId, string status);
}
