namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;

/// <summary>
/// Events of the repair round. They name the attachment reference, which is
/// opaque and already travels in every response, and counts. No storage
/// coordinate appears here: the round works with generations that were never
/// published, and a line that named one would put a coordinate on the record
/// for the first time.
/// </summary>
internal static partial class AttachmentReconciliationScanLogger
{
    [LoggerMessage(
        EventId = 2440,
        Level = LogLevel.Information,
        Message = "Rodada de reconciliação de anexos: {Examined} pendências examinadas, "
            + "{Reclaimed} custódias recuperadas com {Removed} gerações removidas, "
            + "{Closed} esperas encerradas e {Unresolved} sem solução nesta rodada.")]
    internal static partial void AttachmentReconciliationRoundCompleted(
        this ILogger logger,
        int examined,
        int reclaimed,
        int removed,
        int closed,
        int unresolved);

    [LoggerMessage(
        EventId = 2441,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o armazenamento não devolveu o inventário completo da "
            + "chave derivada, portanto nada foi removido e o passivo continua registrado.")]
    internal static partial void AttachmentInventoryUnavailable(
        this ILogger logger,
        string reference);

    [LoggerMessage(
        EventId = 2442,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: o armazenamento não confirmou a remoção de uma geração "
            + "órfã após remover {Removed}, portanto a chave continua ocupada e o passivo "
            + "continua registrado.")]
    internal static partial void AttachmentOrphanNotRemoved(
        this ILogger logger,
        string reference,
        int removed);

    [LoggerMessage(
        EventId = 2443,
        Level = LogLevel.Information,
        Message = "Anexo {Reference}: custódia recuperada com {Removed} gerações removidas; "
            + "a chave derivada voltou a aceitar o envio.")]
    internal static partial void AttachmentCustodyReclaimed(
        this ILogger logger,
        string reference,
        int removed);

    [LoggerMessage(
        EventId = 2444,
        Level = LogLevel.Information,
        Message = "Anexo {Reference}: a espera por um veredito que não concluiu foi encerrada "
            + "pelo prazo.")]
    internal static partial void AttachmentWaitClosed(this ILogger logger, string reference);

    [LoggerMessage(
        EventId = 2445,
        Level = LogLevel.Warning,
        Message = "Anexo {Reference}: a espera não foi encerrada nesta rodada e a validação "
            + "respondeu {Status}.")]
    internal static partial void AttachmentWaitNotClosed(
        this ILogger logger,
        string reference,
        string status);

    [LoggerMessage(
        EventId = 2446,
        Level = LogLevel.Error,
        Message = "Anexo {Reference}: o passivo registrado não pertence ao vocabulário desta "
            + "rodada, portanto nenhum reparo foi executado e o registro permanece intacto.")]
    internal static partial void AttachmentLiabilityNotUnderstood(
        this ILogger logger,
        string reference);
}
