namespace NotificationHub.Api.Modules.Notifications.Features.Ingress;

internal static partial class KafkaIngressProcessorLogger
{
    [LoggerMessage(EventId = 7060, Level = LogLevel.Information, Message = "Evento {Topic}/{Partition}/{Offset} aceito como a notificação {NotificationId}.")]
    internal static partial void IngressEventAccepted(this ILogger logger, string topic, int partition, long offset, Guid notificationId);

    [LoggerMessage(EventId = 7061, Level = LogLevel.Information, Message = "Evento {Topic}/{Partition}/{Offset} é reenvio da notificação {NotificationId}; nenhum efeito novo.")]
    internal static partial void IngressEventReplayed(this ILogger logger, string topic, int partition, long offset, Guid notificationId);
}
