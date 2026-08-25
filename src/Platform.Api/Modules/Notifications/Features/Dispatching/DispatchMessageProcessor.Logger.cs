namespace NotificationHub.Api.Modules.Notifications.Features.Dispatching;

internal static partial class DispatchMessageProcessorLogger
{
    [LoggerMessage(EventId = 7130, Level = LogLevel.Information, Message = "Attempt {AttemptId} da notificação {NotificationId} aceito pelo provedor {ProviderKey}.")]
    internal static partial void DispatchAttemptSent(this ILogger logger, Guid attemptId, Guid notificationId, string providerKey);

    [LoggerMessage(EventId = 7131, Level = LogLevel.Warning, Message = "Attempt {AttemptId} da notificação {NotificationId} falhou definitivamente com o código {ErrorCode}.")]
    internal static partial void DispatchAttemptFailed(this ILogger logger, Guid attemptId, Guid notificationId, string errorCode);

    [LoggerMessage(EventId = 7132, Level = LogLevel.Information, Message = "Attempt {AttemptId} da notificação {NotificationId} devolvido à fila ({Reason}); o provedor não recebeu a chamada.")]
    internal static partial void DispatchAttemptRequeued(this ILogger logger, Guid attemptId, Guid notificationId, string reason);

    [LoggerMessage(EventId = 7133, Level = LogLevel.Warning, Message = "Attempt {AttemptId} da notificação {NotificationId} sem veredito conclusivo ({ErrorCode}); estacionado em unknown para reconciliação.")]
    internal static partial void DispatchAttemptUnknown(this ILogger logger, Guid attemptId, Guid notificationId, string? errorCode);

    [LoggerMessage(EventId = 7134, Level = LogLevel.Information, Message = "Reentrega ignorada: o attempt {AttemptId} está em '{Status}' e não volta ao provedor.")]
    internal static partial void DispatchDuplicateSkipped(this ILogger logger, Guid attemptId, string status);

    [LoggerMessage(EventId = 7135, Level = LogLevel.Information, Message = "Fan-out da notificação {NotificationId} expandido no claim do attempt {AttemptId} para {TokenCount} token(s).")]
    internal static partial void DispatchFanOutExpanded(this ILogger logger, Guid notificationId, Guid attemptId, int tokenCount);

    [LoggerMessage(EventId = 7136, Level = LogLevel.Warning, Message = "Invalidação do token de dispositivo {DeviceTokenId} não registrada: {Error}.")]
    internal static partial void DispatchTokenInvalidationFailed(this ILogger logger, Guid deviceTokenId, string error);

    [LoggerMessage(EventId = 7137, Level = LogLevel.Warning, Message = "Invalidação do token de dispositivo {DeviceTokenId} lançou exceção; a reconciliação de fase posterior cobre a lacuna.")]
    internal static partial void DispatchTokenInvalidationThrew(this ILogger logger, Guid deviceTokenId, Exception exception);

    [LoggerMessage(EventId = 7138, Level = LogLevel.Information, Message = "Attempt {AttemptId} da notificação {NotificationId} no canal {Channel} encerrado sem chamada ao provedor: a validade restante venceu antes do envio.")]
    internal static partial void DispatchAttemptExpired(this ILogger logger, Guid attemptId, Guid notificationId, string channel);
}
