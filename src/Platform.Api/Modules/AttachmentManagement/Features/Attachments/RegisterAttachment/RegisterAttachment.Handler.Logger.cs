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

    [LoggerMessage(
        EventId = 2500,
        Level = LogLevel.Information,
        Message = "Registro de anexo recusado para a aplicação {Application}: a capacidade de "
            + "anexos não está habilitada nesta implantação. Não é bloqueio de emergência e "
            + "nada foi gravado.")]
    internal static partial void AttachmentRegistrationNotEnabled(
        this ILogger logger,
        string application);
}
