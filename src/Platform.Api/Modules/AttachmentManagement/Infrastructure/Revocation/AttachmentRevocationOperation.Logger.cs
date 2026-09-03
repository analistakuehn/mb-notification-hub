namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Revocation;

/// <summary>
/// Events of the revocation path. They follow the rule the upload path decided
/// once: the attachment reference is publishable, because it is opaque and
/// already travels in every response, and the storage coordinate is not. No
/// event here names a generation, a key, a store or a content type.
/// </summary>
internal static partial class AttachmentRevocationLogger
{
    [LoggerMessage(
        EventId = 2440,
        Level = LogLevel.Information,
        Message = "Anexo {Reference}: liberação revogada. Motivo declarado: {Reason}.")]
    internal static partial void AttachmentRevoked(
        this ILogger logger,
        string reference,
        string reason);

    [LoggerMessage(
        EventId = 2441,
        Level = LogLevel.Information,
        Message = "Anexo {Reference}: a liberação já estava revogada, portanto nada foi "
            + "gravado e nenhum registro novo de revogação existe.")]
    internal static partial void AttachmentAlreadyRevoked(this ILogger logger, string reference);

    [LoggerMessage(
        EventId = 2444,
        Level = LogLevel.Information,
        Message = "Anexo {Reference}: nada a revogar no estado {State}; nada foi gravado.")]
    internal static partial void AttachmentNotReleased(
        this ILogger logger,
        string reference,
        string state);

    [LoggerMessage(
        EventId = 2442,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o estado diz liberado e nenhuma liberação o nomeia, "
            + "portanto não há o que revogar de forma nomeável; nada foi gravado.")]
    internal static partial void AttachmentReleaseUnavailable(
        this ILogger logger,
        string reference);

    [LoggerMessage(
        EventId = 2443,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o motivo declarado tem {ReasonLength} caracteres, que o "
            + "estado durável não comporta; nada foi gravado e a liberação continua vigente.")]
    internal static partial void AttachmentRevocationReasonUnusable(
        this ILogger logger,
        string reference,
        int reasonLength);
}
