namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

internal static partial class PendingSuppressionDrainLogger
{
    [LoggerMessage(
        EventId = 7340,
        Level = LogLevel.Information,
        Message = "A varredura de supressões pendentes encontrou {Pending} evidências devendo relato "
            + "e concluiu {Settled}.")]
    internal static partial void PendingSuppressionDrainCompleted(
        this ILogger logger, int pending, int settled);

    [LoggerMessage(
        EventId = 7341,
        Level = LogLevel.Information,
        Message = "O relato pendente da evidência {DeliveryEventId} chegou ao ledger de contatos com o "
            + "desfecho {Outcome} para o ponto de contato {ContactPointId}.")]
    internal static partial void PendingSuppressionReported(
        this ILogger logger, Guid deliveryEventId, Guid contactPointId, string outcome);

    [LoggerMessage(
        EventId = 7342,
        Level = LogLevel.Warning,
        Message = "O relato pendente da evidência {DeliveryEventId} foi recusado pelo ledger de "
            + "contatos: {Reason}. A recusa é uma decisão e a evidência deixa de dever relato.")]
    internal static partial void PendingSuppressionRefused(
        this ILogger logger, Guid deliveryEventId, string reason);

    [LoggerMessage(
        EventId = 7343,
        Level = LogLevel.Warning,
        Message = "O relato pendente da evidência {DeliveryEventId} falhou de novo; a evidência "
            + "continua devendo relato e a próxima rodada tenta outra vez.")]
    internal static partial void PendingSuppressionRetryFailed(
        this ILogger logger, Guid deliveryEventId, Exception exception);
}
