namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

internal static partial class FcmChannelProviderLogger
{
    [LoggerMessage(EventId = 6200, Level = LogLevel.Debug, Message = "FCM aceitou a mensagem (HTTP {StatusCode}).")]
    internal static partial void FcmAccepted(this ILogger logger, int statusCode);

    [LoggerMessage(EventId = 6201, Level = LogLevel.Warning, Message = "FCM não aceitou a mensagem (HTTP {StatusCode}, código {ErrorCode}).")]
    internal static partial void FcmSendFailed(this ILogger logger, int statusCode, string errorCode);

    [LoggerMessage(EventId = 6202, Level = LogLevel.Warning, Message = "Circuito do FCM aberto; envio devolvido como falha transitória sem chamada ao provedor.")]
    internal static partial void FcmCircuitOpen(this ILogger logger);

    [LoggerMessage(EventId = 6203, Level = LogLevel.Warning, Message = "Chamada ao FCM excedeu o timeout de {TimeoutSeconds} segundos.")]
    internal static partial void FcmTimedOut(this ILogger logger, int timeoutSeconds);

    [LoggerMessage(EventId = 6204, Level = LogLevel.Warning, Message = "Falha de rede ao chamar o FCM.")]
    internal static partial void FcmNetworkFault(this ILogger logger, Exception exception);
}
