namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>Log of the scheduler loop: composition and failures, never rows.</summary>
internal static partial class SchedulerScanServiceLogger
{
    [LoggerMessage(
        EventId = 8062,
        Level = LogLevel.Warning,
        Message = "A varredura do scheduler está desligada por configuração; nenhum fallback por prazo "
            + "e nenhuma liberação de adiamento acontecem neste processo.")]
    internal static partial void SchedulerScanDisabled(this ILogger logger);

    [LoggerMessage(
        EventId = 8063,
        Level = LogLevel.Information,
        Message = "Varredura do scheduler iniciada a cada {Interval}, em lotes de {BatchSize}, "
            + "com tolerância de {UnknownGrace} para veredito inconclusivo.")]
    internal static partial void SchedulerScanStarted(
        this ILogger logger,
        TimeSpan interval,
        int batchSize,
        TimeSpan unknownGrace);

    [LoggerMessage(
        EventId = 8064,
        Level = LogLevel.Error,
        Message = "Uma rodada da varredura do scheduler falhou; a próxima tentará de novo.")]
    internal static partial void SchedulerScanRoundFailed(this ILogger logger, Exception exception);
}
