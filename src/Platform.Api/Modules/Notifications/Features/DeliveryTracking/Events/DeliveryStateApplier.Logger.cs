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

    [LoggerMessage(
        EventId = 7095,
        Level = LogLevel.Information,
        Message = "O relato de supressão da evidência {DeliveryEventId} chegou ao ledger de contatos "
            + "com o desfecho {Outcome} para o ponto de contato {ContactPointId}.")]
    internal static partial void SuppressionReported(
        this ILogger logger, Guid deliveryEventId, Guid contactPointId, string outcome);

    [LoggerMessage(
        EventId = 7096,
        Level = LogLevel.Warning,
        Message = "O relato de supressão da evidência {DeliveryEventId} foi recusado pelo ledger de "
            + "contatos: {Reason}. A supressão fica para a reconciliação.")]
    internal static partial void SuppressionReportFailed(
        this ILogger logger, Guid deliveryEventId, string reason);

    [LoggerMessage(
        EventId = 7097,
        Level = LogLevel.Warning,
        Message = "O relato de supressão da evidência {DeliveryEventId} falhou; a transição do attempt "
            + "já foi confirmada e a supressão fica para a reconciliação.")]
    internal static partial void SuppressionReportThrew(
        this ILogger logger, Guid deliveryEventId, Exception exception);

    [LoggerMessage(
        EventId = 7098,
        Level = LogLevel.Information,
        Message = "A evidência {DeliveryEventId} carrega o sinal '{Signal}' e o attempt não endereça um "
            + "ponto de contato; nada é relatado ao ledger de contatos.")]
    internal static partial void SuppressionTargetUnresolved(
        this ILogger logger, Guid deliveryEventId, string signal);

    [LoggerMessage(
        EventId = 7099,
        Level = LogLevel.Warning,
        Message = "O feedback aplicado ao attempt {AttemptId} carrega o sinal '{Signal}' e não guarda "
            + "evidência; nada é relatado, porque o ledger de contatos identifica a recusa pela "
            + "evidência e um identificador cunhado na hora contaria a mesma recusa duas vezes.")]
    internal static partial void SuppressionWithoutEvidence(
        this ILogger logger, Guid attemptId, string signal);
}
