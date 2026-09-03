using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

/// <summary>
/// Events of the upload path.
/// <para>
/// One rule decides what they name. The attachment reference is publishable:
/// it is opaque, it already travels in every response, and without it an event
/// cannot be tied to anything. The storage coordinate is not publishable, and
/// it renders as a fixed placeholder wherever it appears. Every event below
/// follows that one rule, including the ones about failure.
/// </para>
/// </summary>
internal static partial class UploadAttachmentLogger
{
    [LoggerMessage(
        EventId = 2410,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} da aplicação {Application} recebido no estado {State}, com {SizeBytes} bytes.")]
    internal static partial void AttachmentReceived(
        this ILogger logger,
        string reference,
        string application,
        string state,
        long sizeBytes);

    [LoggerMessage(
        EventId = 2411,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: não foi possível confirmar o estado durável após a falha "
            + "de persistência; o objeto foi preservado para reconciliação.")]
    internal static partial void AttachmentCommitStateUnconfirmed(
        this ILogger logger,
        string reference);

    [LoggerMessage(
        EventId = 2412,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} teve a geração verificada e registrada com {LengthBytes} "
            + "bytes; a coordenada de armazenamento não é publicada e aparece como {Locator}.")]
    internal static partial void AttachmentGenerationRecorded(
        this ILogger logger,
        string reference,
        long lengthBytes,
        AttachmentObjectLocator locator);

    [LoggerMessage(
        EventId = 2413,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o armazenamento não confirmou a remoção da geração "
            + "gravada, portanto os bytes continuam a contar como armazenados.")]
    internal static partial void AttachmentGenerationNotRemoved(
        this ILogger logger,
        string reference);

    [LoggerMessage(
        EventId = 2414,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: a remoção da geração gravada lançou, e a falha original "
            + "é a que responde ao chamador.")]
    internal static partial void AttachmentCompensationFailed(
        this ILogger logger,
        Exception exception,
        string reference);

    [LoggerMessage(
        EventId = 2415,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o armazenamento aceitou os bytes sem nomear a geração, "
            + "portanto o conteúdo continua armazenado, fora do registro de gerações e sem "
            + "geração que possa ser removida.")]
    internal static partial void AttachmentBytesLeftWithoutIdentity(
        this ILogger logger,
        string reference);

    [LoggerMessage(
        EventId = 2416,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: a geração que o armazenamento acabara de nomear não foi "
            + "encontrada na leitura de verificação.")]
    internal static partial void AttachmentGenerationVanished(
        this ILogger logger,
        string reference);
}
