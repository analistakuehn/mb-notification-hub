namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

/// <summary>
/// Log of the reconciliation. Identifiers, provider keys, counts and statuses
/// only. This is the one path in the module that transiently holds a contact
/// value, so the absence of it from every placeholder here is a rule and not
/// an accident: the value exists between the reveal and the provider call and
/// must leave no trace behind it.
/// </summary>
internal static partial class DeliveryReconciliationScanLogger
{
    [LoggerMessage(
        EventId = 7186,
        Level = LogLevel.Information,
        Message = "Reconciliação examinou {Examined} tentativas, consultou {Queried}, corrigiu "
            + "{Corrected}, deixou {WithoutLookup} sem consulta possível e retirou {Retired} linhas "
            + "dos índices do scheduler.")]
    internal static partial void ReconciliationRoundCompleted(
        this ILogger logger, int examined, int queried, int corrected, int withoutLookup, int retired);

    [LoggerMessage(
        EventId = 7187,
        Level = LogLevel.Information,
        Message = "O provedor '{ProviderKey}' não oferece consulta posterior; a tentativa {AttemptId} "
            + "permanece em '{Status}' e só o fallback ou a validade a encerram.")]
    internal static partial void ReconciliationWithoutLookup(
        this ILogger logger, Guid attemptId, string providerKey, string status);

    [LoggerMessage(
        EventId = 7188,
        Level = LogLevel.Warning,
        Message = "A consulta ao provedor '{ProviderKey}' sobre a tentativa {AttemptId} não respondeu "
            + "({Reason}); nada foi concluído e a próxima rodada pergunta de novo.")]
    internal static partial void ReconciliationUnanswered(
        this ILogger logger, Guid attemptId, string providerKey, string reason);

    [LoggerMessage(
        EventId = 7189,
        Level = LogLevel.Information,
        Message = "A resposta de '{ProviderKey}' moveu a tentativa {AttemptId} pelo feedback "
            + "'{Kind}'.")]
    internal static partial void ReconciliationCorrected(
        this ILogger logger, Guid attemptId, string providerKey, string kind);

    [LoggerMessage(
        EventId = 7194,
        Level = LogLevel.Information,
        Message = "O evento '{ProviderEventId}' já havia sido honrado por este hub; a tentativa "
            + "{AttemptId} não é tocada uma segunda vez.")]
    internal static partial void ReconciliationAlreadyHonoured(
        this ILogger logger, Guid attemptId, string providerEventId);

    [LoggerMessage(
        EventId = 7195,
        Level = LogLevel.Information,
        Message = "O feedback '{Kind}' encontrado para a tentativa {AttemptId} não tem transição a "
            + "partir de '{Status}'; a evidência fica armazenada e não aplicada.")]
    internal static partial void ReconciliationAnswerIgnored(
        this ILogger logger, Guid attemptId, string kind, string status);

    [LoggerMessage(
        EventId = 7196,
        Level = LogLevel.Warning,
        Message = "O ponto de contato da tentativa {AttemptId} não pôde ser revelado ({Reason}); a "
            + "consulta segue sem destino, e o provedor que precisa dele recusa.")]
    internal static partial void ReconciliationTargetUnavailable(
        this ILogger logger, Guid attemptId, string reason);
}
