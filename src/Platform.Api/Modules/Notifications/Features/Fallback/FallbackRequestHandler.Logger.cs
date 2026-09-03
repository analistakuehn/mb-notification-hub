namespace NotificationHub.Api.Modules.Notifications.Features.Fallback;

internal static partial class FallbackRequestHandlerLogger
{
    [LoggerMessage(EventId = 7140, Level = LogLevel.Information, Message = "Fallback da notificação {NotificationId} enfileirou o attempt {AttemptId} no canal {Channel}.")]
    internal static partial void FallbackAttemptQueued(this ILogger logger, Guid notificationId, Guid attemptId, string channel);

    [LoggerMessage(EventId = 7141, Level = LogLevel.Warning, Message = "Fallback encerrou a notificação {NotificationId} em '{Status}' ({Reason}).")]
    internal static partial void FallbackEndedNotification(this ILogger logger, Guid notificationId, string status, string reason);

    [LoggerMessage(EventId = 7142, Level = LogLevel.Information, Message = "Gatilho de fallback ignorado: a notificação {NotificationId} está em '{Status}'.")]
    internal static partial void FallbackDuplicateSkipped(this ILogger logger, Guid notificationId, string status);

    [LoggerMessage(EventId = 7143, Level = LogLevel.Information, Message = "Gatilho de fallback ignorado: a etapa '{Channel}' da notificação {NotificationId} já havia avançado.")]
    internal static partial void FallbackStepAlreadyAdvanced(this ILogger logger, Guid notificationId, string channel);

    [LoggerMessage(EventId = 7144, Level = LogLevel.Warning, Message = "Gatilho de fallback recusado: o attempt {AttemptId} pertence à notificação {OwnerNotificationId}, não à notificação {NotificationId} do gatilho.")]
    internal static partial void FallbackAttemptNotificationMismatch(this ILogger logger, Guid notificationId, Guid attemptId, Guid ownerNotificationId);

    [LoggerMessage(EventId = 7145, Level = LogLevel.Information, Message = "Fallback da notificação {NotificationId} não encontrou passo utilizável no plano admitido ({Reason}).")]
    internal static partial void FallbackPlanStepBlocked(this ILogger logger, Guid notificationId, string reason);

    [LoggerMessage(EventId = 7146, Level = LogLevel.Information, Message = "A notificação {NotificationId} avançou para '{Channel}' com o attempt {FailedAttemptId} ainda inconclusivo; o risco de mensagem duplicada foi assumido e registrado na trilha.")]
    internal static partial void FallbackRequestedFromUnknown(this ILogger logger, Guid notificationId, Guid failedAttemptId, string channel);

    [LoggerMessage(EventId = 7147, Level = LogLevel.Warning, Message = "O plano admitido da notificação {NotificationId} está ilegível (recusado: '{Refused}'); a decisão seguiu pelo plano publicado, que pode nomear canal já removido na admissão.")]
    internal static partial void FallbackAdmittedPlanUnreadable(this ILogger logger, Guid notificationId, string refused);

    [LoggerMessage(EventId = 7148, Level = LogLevel.Information, Message = "Fallback encerrou a notificação {NotificationId}: o canal '{Channel}' do próximo passo não transporta o conjunto aceito de anexos, e nenhum canal seguinte foi tentado.")]
    internal static partial void FallbackStepCannotCarryAttachments(this ILogger logger, Guid notificationId, string channel);
}
