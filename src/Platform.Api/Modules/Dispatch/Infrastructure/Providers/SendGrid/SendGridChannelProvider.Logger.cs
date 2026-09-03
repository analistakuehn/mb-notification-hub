using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

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

    [LoggerMessage(
        EventId = 6105,
        Level = LogLevel.Warning,
        Message = "A mensagem composta ultrapassa o teto de corpo de {CeilingBytes} bytes do "
            + "SendGrid; envio recusado antes de qualquer chamada, com {AttachmentCount} "
            + "anexo(s) no conjunto.")]
    internal static partial void SendGridMessageOverCeiling(
        this ILogger logger,
        long ceilingBytes,
        int attachmentCount);

    [LoggerMessage(
        EventId = 6106,
        Level = LogLevel.Warning,
        Message = "O corpo do envio ao SendGrid foi interrompido ({Reason}); a requisição não "
            + "se completou e o provedor não deu veredito.")]
    internal static partial void SendGridBodyInterrupted(this ILogger logger, string reason);

    /// <summary>
    /// The witness of one attempt, on the line of the side that made the call.
    /// It names the notification and the attempt, which is what ties the
    /// verdict to an attempt, and it is the only line that can: the module
    /// that owns the proof of the bytes settles the comparison and does not
    /// know which attempt asked.
    /// <para>
    /// It names no handle, no file name, no media type, no length and no
    /// digest in any spelling. A verdict is a word, and a word is what this
    /// surface may carry.
    /// </para>
    /// </summary>
    [LoggerMessage(
        EventId = 6107,
        Level = LogLevel.Information,
        Message = "Testemunha do conjunto submetido ao SendGrid: {Verdict} sobre "
            + "{MemberCount} anexo(s), na notificação {NotificationId}, tentativa "
            + "{AttemptId}.")]
    internal static partial void SendGridSubmissionSettled(
        this ILogger logger,
        AttachmentSubmissionVerdict verdict,
        int memberCount,
        Guid? notificationId,
        Guid? attemptId);

    [LoggerMessage(
        EventId = 6108,
        Level = LogLevel.Warning,
        Message = "A testemunha do conjunto submetido ao SendGrid foi descartada por "
            + "cancelamento depois do veredito do provedor, na notificação {NotificationId}, "
            + "tentativa {AttemptId}; o envio mantém o desfecho que o provedor deu.")]
    internal static partial void SendGridSubmissionWitnessDropped(
        this ILogger logger,
        Guid? notificationId,
        Guid? attemptId);
}
