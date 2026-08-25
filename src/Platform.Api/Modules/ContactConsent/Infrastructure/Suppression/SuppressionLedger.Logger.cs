namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Suppression;

/// <summary>
/// Log of the suppression ledger. Identifiers, channel and classification
/// only: which address stopped working is contact data and never reaches a
/// log line.
/// </summary>
internal static partial class SuppressionLedgerLogger
{
    [LoggerMessage(
        EventId = 8032,
        Level = LogLevel.Information,
        Message = "Sinal de supressão registrado para o ponto de contato {ContactPointId} do destinatário "
            + "{RecipientId} ({Reason}); {Occurrences} ocorrência(s) acumuladas, abaixo do limite do canal.")]
    internal static partial void SuppressionSignalRecorded(
        this ILogger logger, string recipientId, Guid contactPointId, string reason, int occurrences);

    [LoggerMessage(
        EventId = 8033,
        Level = LogLevel.Warning,
        Message = "Ponto de contato {ContactPointId} do destinatário {RecipientId} suprimido no canal "
            + "{Channel} ({Reason}); o canal deixa de ser elegível até remoção manual.")]
    internal static partial void ContactSuppressed(
        this ILogger logger, string recipientId, Guid contactPointId, string channel, string reason);

    [LoggerMessage(
        EventId = 8034,
        Level = LogLevel.Information,
        Message = "Relato de supressão repetido para o ponto de contato {ContactPointId} do destinatário "
            + "{RecipientId} (evento de origem {SourceEventId}); nenhum efeito novo.")]
    internal static partial void SuppressionReportRepeated(
        this ILogger logger, string recipientId, Guid contactPointId, Guid sourceEventId);

    [LoggerMessage(
        EventId = 8035,
        Level = LogLevel.Warning,
        Message = "Escrita concorrente venceu a corrida do relato de supressão do ponto de contato "
            + "{ContactPointId} do destinatário {RecipientId}; o relato precisa ser repetido.")]
    internal static partial void SuppressionWriteConflict(
        this ILogger logger, string recipientId, Guid contactPointId);
}
