namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>
/// Log of the delivery-feedback consumer. Identifiers only: the evidence row
/// carries the provider payload sealed, and this path never opens it.
/// </summary>
internal static partial class DeliveryEventMessageProcessorLogger
{
    [LoggerMessage(
        EventId = 7080,
        Level = LogLevel.Information,
        Message = "A evidência {DeliveryEventId} ainda não encontra o attempt; nova tentativa em "
            + "{Delay}.")]
    internal static partial void DeliveryEventPostponed(
        this ILogger logger, Guid deliveryEventId, TimeSpan delay);

    [LoggerMessage(
        EventId = 7081,
        Level = LogLevel.Warning,
        Message = "A evidência {DeliveryEventId} completou a janela de {Window} sem encontrar o "
            + "attempt; permanece armazenada e não aplicada.")]
    internal static partial void DeliveryEventAbandoned(
        this ILogger logger, Guid deliveryEventId, TimeSpan window);

    [LoggerMessage(
        EventId = 7082,
        Level = LogLevel.Information,
        Message = "A evidência {DeliveryEventId} já estava aplicada; nenhuma transição repetida.")]
    internal static partial void DeliveryEventAlreadyApplied(this ILogger logger, Guid deliveryEventId);

    [LoggerMessage(
        EventId = 7083,
        Level = LogLevel.Warning,
        Message = "A evidência {DeliveryEventId} guarda o tipo de feedback '{Kind}', que não pertence "
            + "ao vocabulário canônico; a mensagem é descartada.")]
    internal static partial void DeliveryEventKindUnknown(
        this ILogger logger, Guid deliveryEventId, string kind);

    [LoggerMessage(
        EventId = 7084,
        Level = LogLevel.Information,
        Message = "O relato de supressão da evidência {DeliveryEventId} chegou ao ledger de contatos "
            + "com o desfecho {Outcome} para o ponto de contato {ContactPointId}.")]
    internal static partial void SuppressionReported(
        this ILogger logger, Guid deliveryEventId, Guid contactPointId, string outcome);

    [LoggerMessage(
        EventId = 7085,
        Level = LogLevel.Warning,
        Message = "O relato de supressão da evidência {DeliveryEventId} foi recusado pelo ledger de "
            + "contatos: {Reason}. A supressão fica para a reconciliação.")]
    internal static partial void SuppressionReportFailed(
        this ILogger logger, Guid deliveryEventId, string reason);

    [LoggerMessage(
        EventId = 7086,
        Level = LogLevel.Warning,
        Message = "O relato de supressão da evidência {DeliveryEventId} falhou; a transição do attempt "
            + "já foi confirmada e a supressão fica para a reconciliação.")]
    internal static partial void SuppressionReportThrew(
        this ILogger logger, Guid deliveryEventId, Exception exception);

    [LoggerMessage(
        EventId = 7087,
        Level = LogLevel.Information,
        Message = "A evidência {DeliveryEventId} carrega o sinal '{Signal}' e o attempt não endereça um "
            + "ponto de contato; nada é relatado ao ledger de contatos.")]
    internal static partial void SuppressionTargetUnresolved(
        this ILogger logger, Guid deliveryEventId, string signal);
}
