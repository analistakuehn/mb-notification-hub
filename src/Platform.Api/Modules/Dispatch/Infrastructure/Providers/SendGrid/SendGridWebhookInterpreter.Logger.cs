namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

internal static partial class SendGridWebhookInterpreterLogger
{
    [LoggerMessage(EventId = 6420, Level = LogLevel.Debug, Message = "Callback de eventos do SendGrid verificado.")]
    internal static partial void SendGridWebhookVerified(this ILogger logger);

    [LoggerMessage(EventId = 6421, Level = LogLevel.Warning, Message = "Callback do SendGrid recusado: origem fora da allowlist configurada.")]
    internal static partial void SendGridWebhookOriginRejected(this ILogger logger);

    [LoggerMessage(EventId = 6422, Level = LogLevel.Warning, Message = "Callback do SendGrid recusado: assinatura não confere.")]
    internal static partial void SendGridWebhookSignatureRejected(this ILogger logger);

    [LoggerMessage(EventId = 6423, Level = LogLevel.Warning, Message = "Callback do SendGrid recusado: carimbo de tempo ausente ou fora da janela de {WindowSeconds} segundos.")]
    internal static partial void SendGridWebhookTimestampOutOfWindow(this ILogger logger, int windowSeconds);

    [LoggerMessage(EventId = 6424, Level = LogLevel.Warning, Message = "Callback do SendGrid recusado: lote ilegível como JSON de eventos.")]
    internal static partial void SendGridWebhookPayloadUnreadable(this ILogger logger);

    [LoggerMessage(EventId = 6425, Level = LogLevel.Error, Message = "Chave pública de verificação do SendGrid ausente ou ilegível em {SectionName}; todo callback será recusado até a configuração chegar.")]
    internal static partial void SendGridWebhookKeyUnusable(this ILogger logger, string sectionName);

    [LoggerMessage(EventId = 6426, Level = LogLevel.Warning, Message = "Evento {EventName} do SendGrid está fora do vocabulário mapeado e fora da lista de eventos não rastreados; nada foi produzido para ele.")]
    internal static partial void SendGridWebhookEventUnmapped(this ILogger logger, string eventName);
}
