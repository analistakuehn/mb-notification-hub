namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;

/// <summary>Log of the reconciliation scheduler: configuration and round faults only.</summary>
internal static partial class DeliveryReconciliationServiceLogger
{
    [LoggerMessage(
        EventId = 7197,
        Level = LogLevel.Information,
        Message = "Reconciliação de entrega desabilitada por configuração.")]
    internal static partial void ReconciliationDisabled(this ILogger logger);

    [LoggerMessage(
        EventId = 7198,
        Level = LogLevel.Information,
        Message = "Reconciliação de entrega iniciada com intervalo {Interval}, carência {StaleAfter} "
            + "e lote de {BatchSize} tentativas.")]
    internal static partial void ReconciliationStarted(
        this ILogger logger, TimeSpan interval, TimeSpan staleAfter, int batchSize);

    [LoggerMessage(
        EventId = 7199,
        Level = LogLevel.Error,
        Message = "Falha em uma rodada da reconciliação de entrega; nova tentativa no próximo ciclo.")]
    internal static partial void ReconciliationRoundFailed(this ILogger logger, Exception exception);
}
