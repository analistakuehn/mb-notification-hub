namespace NotificationHub.Api.Modules.Notifications.Features.Dispatching;

/// <summary>
/// The one event of the revalidation that nothing downstream would otherwise
/// record. A refusal that settles the attempt is already told by the
/// settlement, with the stable code on it; this one settles nothing.
/// </summary>
internal static partial class AttachmentPreflightLogger
{
    [LoggerMessage(
        EventId = 7139,
        Level = LogLevel.Error,
        Message = "A notificação {NotificationId} passou a carregar um conjunto aceito "
            + "ilegível depois do claim do attempt; nada foi afirmado sobre o conjunto e o "
            + "attempt volta à fila, onde a recusa anterior ao claim relata o defeito.")]
    internal static partial void PreflightMetAnUnreadableSet(
        this ILogger logger,
        Guid notificationId);
}
