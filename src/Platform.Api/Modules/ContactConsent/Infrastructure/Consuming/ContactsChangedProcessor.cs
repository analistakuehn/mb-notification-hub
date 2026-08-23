using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;

/// <summary>
/// Consumer of the contacts-changed queue: every write this module commits
/// emits an invalidation message, and this processor marks the cached
/// snapshot stale so the next read revalidates against the store. The mark
/// runs before the processed mark on purpose: marking stale is idempotent,
/// so a crash between the two only repeats the invalidation, never loses it.
/// </summary>
internal sealed class ContactsChangedProcessor(
    ContactConsentDbContext db,
    RecipientSnapshotCache cache,
    IProcessedMessageStore processedMessages,
    ILogger<ContactsChangedProcessor> logger) : ISqsMessageProcessor
{
    internal const string ConsumerName = "contact-consent-cache";
    internal const int SupportedSchemaVersion = 1;
    internal const string ReasonPayloadWithoutRecipientId = "payload-missing-recipient-id";

    public string Consumer => ConsumerName;

    public bool Accepts(string type, int schemaVersion)
        => schemaVersion == SupportedSchemaVersion
            && (string.Equals(type, ContactConsentEvents.ContactChanged, StringComparison.Ordinal)
                || string.Equals(type, ContactConsentEvents.ConsentChanged, StringComparison.Ordinal));

    public async Task<MessageDisposition> ProcessAsync(
        MessageEnvelope envelope,
        CancellationToken cancellationToken)
    {
        if (!envelope.Payload.TryGetProperty("recipientId", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.String
            || idElement.GetString() is not { Length: > 0 } recipientId)
        {
            return new MessageDisposition.Discard(ReasonPayloadWithoutRecipientId);
        }

        await cache.MarkStaleAsync(recipientId, cancellationToken);
        logger.SnapshotInvalidated(recipientId, envelope.Type);

        // The effect above is idempotent, so the mark rides its own short
        // transaction: a duplicate here only means the invalidation already
        // ran, which is exactly the desired state.
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(),
            $"{envelope.MessageId:N}:{recipientId}",
            ConsumerName,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return marked ? new MessageDisposition.Processed() : new MessageDisposition.Duplicate();
    }
}
