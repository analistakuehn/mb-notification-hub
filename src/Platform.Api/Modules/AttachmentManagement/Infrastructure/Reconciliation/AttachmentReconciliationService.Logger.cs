namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;

/// <summary>Events of the scheduler that drives the repair round.</summary>
internal static partial class AttachmentReconciliationServiceLogger
{
    [LoggerMessage(
        EventId = 2450,
        Level = LogLevel.Warning,
        Message = "A reconciliação de anexos está desligada nesta implantação, portanto os "
            + "passivos registrados permanecem no banco sem que ninguém os execute.")]
    internal static partial void AttachmentReconciliationDisabled(this ILogger logger);

    [LoggerMessage(
        EventId = 2451,
        Level = LogLevel.Information,
        Message = "Reconciliação de anexos iniciada com intervalo de {Interval} e lote de "
            + "{BatchSize} pendências por rodada.")]
    internal static partial void AttachmentReconciliationStarted(
        this ILogger logger,
        TimeSpan interval,
        int batchSize);

    [LoggerMessage(
        EventId = 2452,
        Level = LogLevel.Error,
        Message = "A rodada de reconciliação de anexos falhou; os passivos continuam "
            + "registrados e a próxima rodada os encontra.")]
    internal static partial void AttachmentReconciliationRoundFailed(
        this ILogger logger,
        Exception exception);
}
