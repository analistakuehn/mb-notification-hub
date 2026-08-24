namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

internal static partial class TwilioChannelProviderLogger
{
    [LoggerMessage(EventId = 6300, Level = LogLevel.Debug, Message = "Twilio aceitou a mensagem SMS (HTTP {StatusCode}).")]
    internal static partial void TwilioAccepted(this ILogger logger, int statusCode);

    [LoggerMessage(EventId = 6301, Level = LogLevel.Warning, Message = "Twilio não aceitou a mensagem SMS (HTTP {StatusCode}, código {ErrorCode}).")]
    internal static partial void TwilioSendFailed(this ILogger logger, int statusCode, string errorCode);

    [LoggerMessage(EventId = 6302, Level = LogLevel.Warning, Message = "Circuito do Twilio aberto; envio SMS devolvido como falha transitória sem chamada ao provedor.")]
    internal static partial void TwilioCircuitOpen(this ILogger logger);

    [LoggerMessage(EventId = 6303, Level = LogLevel.Warning, Message = "Chamada ao Twilio excedeu o timeout de {TimeoutSeconds} segundos.")]
    internal static partial void TwilioTimedOut(this ILogger logger, int timeoutSeconds);

    [LoggerMessage(EventId = 6304, Level = LogLevel.Warning, Message = "Falha de rede ao chamar o Twilio para SMS.")]
    internal static partial void TwilioNetworkFault(this ILogger logger, Exception exception);
}
