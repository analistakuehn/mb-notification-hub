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
}
