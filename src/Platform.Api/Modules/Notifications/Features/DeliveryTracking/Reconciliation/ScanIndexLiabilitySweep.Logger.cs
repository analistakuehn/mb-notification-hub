namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

/// <summary>
/// Log of the index retirement. Counts only: the sweep reads no content, no
/// destination and no recipient.
/// </summary>
internal static partial class ScanIndexLiabilitySweepLogger
{
    [LoggerMessage(
        EventId = 7185,
        Level = LogLevel.Information,
        Message = "Retiradas {Retired} tentativas de notificações já encerradas dos índices do "
            + "scheduler; elas guardavam prazo de fallback com avanço de plano vazio e eram lidas "
            + "e descartadas a cada rodada.")]
    internal static partial void ScanIndexLiabilityRetired(this ILogger logger, int retired);
}
