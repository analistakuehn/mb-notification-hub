namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

/// <summary>
/// Events of the validation path. They follow the rule the upload path decided
/// once: the attachment reference is publishable, because it is opaque and
/// already travels in every response, and the storage coordinate is not. No
/// event here names a generation, a key, a store or a content type.
/// </summary>
internal static partial class AttachmentValidationLogger
{
    [LoggerMessage(
        EventId = 2430,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} liberado por aprovação explícita da política.")]
    internal static partial void AttachmentReleased(this ILogger logger, string reference);

    [LoggerMessage(
        EventId = 2431,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} recusado de forma definitiva: {Detail}.")]
    internal static partial void AttachmentRejected(
        this ILogger logger,
        string reference,
        string detail);

    [LoggerMessage(
        EventId = 2432,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} sem veredito conclusivo: {Detail}. A espera termina em "
            + "{Deadline} e o anexo não é liberado até lá.")]
    internal static partial void AttachmentVerdictOpen(
        this ILogger logger,
        string reference,
        string detail,
        DateTimeOffset? deadline);

    [LoggerMessage(
        EventId = 2433,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: a política de conteúdo lançou, portanto não houve "
            + "veredito e o anexo continua não liberado.")]
    internal static partial void AttachmentPolicyFailed(
        this ILogger logger,
        Exception exception,
        string reference);

    [LoggerMessage(
        EventId = 2434,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: a política de conteúdo não devolveu veredito algum, "
            + "portanto o anexo continua não liberado.")]
    internal static partial void AttachmentPolicyAnsweredNothing(
        this ILogger logger,
        string reference);

    [LoggerMessage(
        EventId = 2435,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: a política de conteúdo devolveu um detalhe de "
            + "{DetailLength} caracteres, que o estado durável não comporta; nada foi gravado "
            + "e o anexo continua não liberado.")]
    internal static partial void AttachmentPolicyDetailUnusable(
        this ILogger logger,
        string reference,
        int detailLength);

    [LoggerMessage(
        EventId = 2436,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: {GenerationCount} gerações registradas, portanto não há "
            + "uma identidade única sobre a qual decidir; nada foi gravado.")]
    internal static partial void AttachmentIdentityUnavailable(
        this ILogger logger,
        string reference,
        int generationCount);
}
