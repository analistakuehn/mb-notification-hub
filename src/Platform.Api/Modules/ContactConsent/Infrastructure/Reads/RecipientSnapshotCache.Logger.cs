namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;

internal static partial class RecipientSnapshotCacheLogger
{
    [LoggerMessage(EventId = 7201, Level = LogLevel.Warning, Message = "Cache de snapshot indisponível para o destinatário {RecipientId}; seguindo em fail-open contra o armazenamento local.")]
    internal static partial void SnapshotCacheUnavailable(this ILogger logger, string recipientId, Exception exception);
}
