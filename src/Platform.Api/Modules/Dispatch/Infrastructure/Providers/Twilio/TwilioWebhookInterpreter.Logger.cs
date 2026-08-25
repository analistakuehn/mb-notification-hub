namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

internal static partial class TwilioWebhookInterpreterLogger
{
    [LoggerMessage(EventId = 6410, Level = LogLevel.Debug, Message = "Callback de entrega do Twilio verificado.")]
    internal static partial void TwilioWebhookVerified(this ILogger logger);

    [LoggerMessage(EventId = 6411, Level = LogLevel.Warning, Message = "Callback do Twilio recusado: origem fora da allowlist configurada.")]
    internal static partial void TwilioWebhookOriginRejected(this ILogger logger);

    [LoggerMessage(EventId = 6412, Level = LogLevel.Warning, Message = "Callback do Twilio recusado: assinatura não confere.")]
    internal static partial void TwilioWebhookSignatureRejected(this ILogger logger);

    [LoggerMessage(EventId = 6413, Level = LogLevel.Warning, Message = "Callback do Twilio recusado: carimbo de tempo fora da janela de {WindowSeconds} segundos.")]
    internal static partial void TwilioWebhookTimestampOutOfWindow(this ILogger logger, int windowSeconds);

    [LoggerMessage(EventId = 6414, Level = LogLevel.Warning, Message = "Callback do Twilio recusado: corpo ilegível como formulário codificado.")]
    internal static partial void TwilioWebhookPayloadUnreadable(this ILogger logger);

    [LoggerMessage(EventId = 6415, Level = LogLevel.Error, Message = "Segredo de verificação do Twilio ausente em {SectionName}; todo callback será recusado até a configuração chegar.")]
    internal static partial void TwilioWebhookSecretMissing(this ILogger logger, string sectionName);

    [LoggerMessage(EventId = 6416, Level = LogLevel.Warning, Message = "Status {Status} do Twilio está fora do vocabulário mapeado; o callback foi recusado em vez de adivinhar o estado.")]
    internal static partial void TwilioWebhookStatusUnmapped(this ILogger logger, string status);
}
