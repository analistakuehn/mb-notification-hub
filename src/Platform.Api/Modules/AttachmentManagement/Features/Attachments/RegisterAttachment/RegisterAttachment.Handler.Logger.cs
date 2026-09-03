namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RegisterAttachmentLogger
{
    [LoggerMessage(
        EventId = 2400,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} registrado para a aplicação {Application} no estado {State}, com tamanho declarado de {SizeBytes} bytes.")]
    internal static partial void AttachmentRegistered(
        this ILogger logger,
        string reference,
        string application,
        string state,
        long sizeBytes);
}
