namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;

internal static partial class ContactsChangedProcessorLogger
{
    [LoggerMessage(EventId = 7210, Level = LogLevel.Information, Message = "Snapshot do destinatário {RecipientId} invalidado pelo evento {EventType}.")]
    internal static partial void SnapshotInvalidated(this ILogger logger, string recipientId, string eventType);
}
