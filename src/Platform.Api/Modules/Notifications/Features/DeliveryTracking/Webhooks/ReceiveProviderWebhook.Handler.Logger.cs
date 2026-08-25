namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Webhooks;

/// <summary>
/// Log of the delivery-feedback ingestion. Only identifiers and counts:
/// the provider payload carries the destination in the clear, so nothing
/// derived from it ever becomes a placeholder here.
/// </summary>
internal static partial class ReceiveProviderWebhookLogger
{
    [LoggerMessage(
        EventId = 7190,
        Level = LogLevel.Information,
        Message = "Callback de '{ProviderKey}' aceito com {Received} eventos: "
            + "{Stored} armazenados e {Duplicated} já conhecidos.")]
    internal static partial void DeliveryWebhookAccepted(
        this ILogger logger, string providerKey, int received, int stored, int duplicated);

    [LoggerMessage(
        EventId = 7191,
        Level = LogLevel.Debug,
        Message = "Callback de '{ProviderKey}' não trouxe nenhum evento rastreado; nada a armazenar.")]
    internal static partial void DeliveryWebhookEmptyBatch(this ILogger logger, string providerKey);

    [LoggerMessage(
        EventId = 7192,
        Level = LogLevel.Debug,
        Message = "Evento de '{ProviderKey}' já constava no livro de deduplicação; nenhum efeito novo.")]
    internal static partial void DeliveryWebhookEventDuplicated(this ILogger logger, string providerKey);

    [LoggerMessage(
        EventId = 7193,
        Level = LogLevel.Warning,
        Message = "Callback autenticado de '{ProviderKey}' recusado na tradução com o motivo '{Refusal}'.")]
    internal static partial void DeliveryWebhookUnreadable(
        this ILogger logger, string providerKey, string refusal);

    [LoggerMessage(
        EventId = 7194,
        Level = LogLevel.Warning,
        Message = "Callback de '{ProviderKey}' trouxe correlação na rota, que a assinatura "
            + "deste provedor não cobre; a correlação foi descartada e só o corpo assinado vale.")]
    internal static partial void DeliveryWebhookRouteCorrelationIgnored(
        this ILogger logger, string providerKey);

    [LoggerMessage(
        EventId = 7195,
        Level = LogLevel.Warning,
        Message = "Callback de '{ProviderKey}' trouxe {Received} eventos rastreados, acima do teto de "
            + "{Maximum} por chamada; o lote inteiro foi recusado.")]
    internal static partial void DeliveryWebhookBatchTooLarge(
        this ILogger logger, string providerKey, int received, int maximum);
}
