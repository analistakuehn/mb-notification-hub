namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

internal static partial class SendGridChannelProviderLogger
{
    [LoggerMessage(EventId = 6100, Level = LogLevel.Debug, Message = "SendGrid aceitou a mensagem (HTTP {StatusCode}).")]
    internal static partial void SendGridAccepted(this ILogger logger, int statusCode);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "SendGrid não aceitou a mensagem (HTTP {StatusCode}, código {ErrorCode}).")]
    internal static partial void SendGridSendFailed(this ILogger logger, int statusCode, string errorCode);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Warning, Message = "Circuito do SendGrid aberto; envio devolvido como falha transitória sem chamada ao provedor.")]
    internal static partial void SendGridCircuitOpen(this ILogger logger);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Warning, Message = "Chamada ao SendGrid excedeu o timeout de {TimeoutSeconds} segundos.")]
    internal static partial void SendGridTimedOut(this ILogger logger, int timeoutSeconds);

    [LoggerMessage(EventId = 6104, Level = LogLevel.Warning, Message = "Falha de rede ao chamar o SendGrid.")]
    internal static partial void SendGridNetworkFault(this ILogger logger, Exception exception);
}
