namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;

internal static partial class CachedRecipientDirectoryLogger
{
    [LoggerMessage(EventId = 7200, Level = LogLevel.Error, Message = "Leitura local de contatos degradada para o destinatário {RecipientId}; servindo o último valor conhecido de {CachedAt}.")]
    internal static partial void ServedLastKnownSnapshot(this ILogger logger, string recipientId, DateTimeOffset cachedAt, Exception exception);
}
