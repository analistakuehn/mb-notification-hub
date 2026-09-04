namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;

/// <summary>
/// Events of the sweep of abandoned attachments. They name the reference,
/// which is opaque and already travels in every response, the state the
/// attachment was in, and counts. No storage coordinate appears here: the
/// sweep removes generations that were never published, and a line naming one
/// would put a coordinate on the record for the first time.
/// </summary>
internal static partial class AttachmentAbandonmentScanLogger
{
    [LoggerMessage(
        EventId = 2480,
        Level = LogLevel.Information,
        Message = "Rodada de descarte de abandonados: {Examined} candidatos examinados, "
            + "{Discarded} anexos descartados, {Generations} gerações registradas e "
            + "{Unrecorded} não registradas removidas, {Preserved} preservados por "
            + "dependência ativa e {Unresolved} sem conclusão nesta rodada.")]
    internal static partial void AttachmentAbandonmentRoundCompleted(
        this ILogger logger,
        int examined,
        int discarded,
        int generations,
        int unrecorded,
        int preserved,
        int unresolved);

    [LoggerMessage(
        EventId = 2481,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} descartado no estado {State}: {Generations} gerações "
            + "registradas e {Unrecorded} não registradas removidas; o registro permanece.")]
    internal static partial void AttachmentDiscarded(
        this ILogger logger,
        string reference,
        string state,
        int generations,
        int unrecorded);

    [LoggerMessage(
        EventId = 2482,
        Level = LogLevel.Information,
        Message = "Anexo {Reference} deixou de estar abandonado antes de a trava ser tomada "
            + "e agora está em {State}; nada foi removido.")]
    internal static partial void AttachmentNoLongerAbandoned(
        this ILogger logger,
        string reference,
        string state);

    [LoggerMessage(
        EventId = 2483,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o armazenamento não confirmou a remoção de "
            + "{Unconfirmed} gerações registradas, portanto o descarte não foi concluído "
            + "e o anexo continua no estado em que estava.")]
    internal static partial void AttachmentRemovalUnconfirmed(
        this ILogger logger,
        string reference,
        int unconfirmed);

    [LoggerMessage(
        EventId = 2484,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o armazenamento não devolveu o inventário completo da "
            + "chave derivada, portanto não é possível afirmar que a chave ficou vazia e o "
            + "descarte não foi concluído.")]
    internal static partial void AttachmentKeyNotListed(this ILogger logger, string reference);

    [LoggerMessage(
        EventId = 2485,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o armazenamento não confirmou a remoção de uma geração "
            + "não registrada após remover {Removed}, portanto o descarte não foi concluído.")]
    internal static partial void AttachmentUnrecordedNotRemoved(
        this ILogger logger,
        string reference,
        int removed);

    [LoggerMessage(
        EventId = 2486,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: nenhuma linha respondeu à trava, portanto o candidato "
            + "saiu desta rodada sem nenhuma remoção.")]
    internal static partial void AttachmentNoLongerPresent(this ILogger logger, string reference);
}
