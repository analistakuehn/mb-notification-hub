namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline;

internal static partial class CoreMessageProcessorLogger
{
    [LoggerMessage(EventId = 7120, Level = LogLevel.Information, Message = "Pipeline concluído para a notificação {NotificationId} da classe {Class} com desfecho {Kind}.")]
    internal static partial void PipelineCompleted(this ILogger logger, Guid notificationId, string @class, string kind);

    [LoggerMessage(EventId = 7121, Level = LogLevel.Information, Message = "Reentrega da notificação {NotificationId} ignorada: estado atual '{Status}' já é resultado de um commit anterior.")]
    internal static partial void PipelineDuplicateSkipped(this ILogger logger, Guid notificationId, string status);
}
