namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// Log of the overdue scans. Counts and ages only: the scan reads a
/// recipient identifier to key its queue message and never puts one in a
/// placeholder, and it opens no content at all.
/// </summary>
internal static partial class OverdueFallbackScanLogger
{
    /// <summary>
    /// The observable signal of a scheduler falling behind. The counts alone
    /// do not show it, because a scheduler that is keeping up and one that is
    /// hopelessly behind both report full batches; the age of the oldest row
    /// the round found is what separates them, and it is what an operator
    /// alarms on.
    /// </summary>
    [LoggerMessage(
        EventId = 8060,
        Level = LogLevel.Information,
        Message = "Varredura de fallback pediu {ByDeadline} por prazo vencido e {ByAge} por veredito "
            + "inconclusivo, liberou {Released} pedidos sem resposta; a linha vencida mais antiga "
            + "esperava há {OldestOverdue}.")]
    internal static partial void OverdueFallbackRequested(
        this ILogger logger,
        int byDeadline,
        int byAge,
        int released,
        TimeSpan oldestOverdue);
}
