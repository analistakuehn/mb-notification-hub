namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// Log of the release scan. Counts and ages only: the scan reads a recipient
/// identifier to key its queue message and never puts one in a placeholder.
/// </summary>
internal static partial class DeferredReleaseScanLogger
{
    [LoggerMessage(
        EventId = 8061,
        Level = LogLevel.Information,
        Message = "Varredura de liberação devolveu {Released} notificações adiadas ao pipeline; "
            + "a mais antiga esperava há {OldestOverdue} além do instante de liberação.")]
    internal static partial void DeferredNotificationsReleased(
        this ILogger logger,
        int released,
        TimeSpan oldestOverdue);
}
