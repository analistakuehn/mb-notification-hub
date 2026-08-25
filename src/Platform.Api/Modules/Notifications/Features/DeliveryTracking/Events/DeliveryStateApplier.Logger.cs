namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>
/// Log of the state application. Identifiers, statuses and provider keys
/// only: the sealed provider payload never opens on this path, and nothing
/// that identifies a person may become a placeholder here.
/// </summary>
internal static partial class DeliveryStateApplierLogger
{
    [LoggerMessage(
        EventId = 7090,
        Level = LogLevel.Information,
        Message = "Feedback de '{ProviderKey}' moveu o attempt {AttemptId} de '{FromStatus}' "
            + "para '{ToStatus}'.")]
    internal static partial void DeliveryTransitionApplied(
        this ILogger logger, string providerKey, Guid attemptId, string fromStatus, string toStatus);

    [LoggerMessage(
        EventId = 7091,
        Level = LogLevel.Information,
        Message = "Feedback '{Kind}' de '{ProviderKey}' não tem transição válida a partir de "
            + "'{FromStatus}' no attempt {AttemptId}; registrado e ignorado.")]
    internal static partial void DeliveryTransitionNotApplicable(
        this ILogger logger, string kind, string providerKey, string fromStatus, Guid attemptId);

    [LoggerMessage(
        EventId = 7092,
        Level = LogLevel.Information,
        Message = "Feedback de '{ProviderKey}' ainda não encontra o attempt correspondente; "
            + "a evidência permanece armazenada e não aplicada.")]
    internal static partial void DeliveryAttemptUnresolved(this ILogger logger, string providerKey);

    [LoggerMessage(
        EventId = 7093,
        Level = LogLevel.Warning,
        Message = "O attempt {AttemptId} saiu de '{FromStatus}' durante a aplicação do feedback; "
            + "nenhuma transição foi escrita.")]
    internal static partial void DeliveryTransitionRaced(
        this ILogger logger, Guid attemptId, string fromStatus);

    [LoggerMessage(
        EventId = 7094,
        Level = LogLevel.Warning,
        Message = "O attempt {AttemptId} aponta para a notificação {NotificationId}, que não existe; "
            + "o feedback fica sem trilha.")]
    internal static partial void DeliveryNotificationMissing(
        this ILogger logger, Guid attemptId, Guid notificationId);
}
