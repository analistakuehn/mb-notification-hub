using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>How one piece of provider feedback settled on its way into the store.</summary>
internal enum DeliveryEventRecordOutcome
{
    /// <summary>The evidence is stored and announced for application.</summary>
    Stored = 0,

    /// <summary>
    /// This provider already delivered this event: the ledger refused the
    /// identity, nothing was written and nothing was announced.
    /// </summary>
    Duplicate = 1,
}

/// <summary>
/// What one write of provider feedback produced: how it settled, and the
/// evidence row it created when it created one. The identity matters to a
/// caller that goes on to apply the event itself, because the contact ledger
/// keys the idempotency of a refusal on the evidence row that originated it.
/// </summary>
internal readonly record struct DeliveryEventRecorded(
    DeliveryEventRecordOutcome Outcome,
    Guid? DeliveryEventId);

/// <summary>
/// Transactional write of the delivery-tracking ingestion. One callback event
/// commits together or not at all: the deduplication mark that claims the
/// provider's event identity, the sealed callback bytes when this is the event
/// that first claims one, the evidence row that references them, and the queue
/// message that hands the event to its asynchronous application.
/// <para>
/// The bytes are written once per callback rather than once per event. The
/// property that an event's evidence is the whole batch that carried it is
/// preserved by ordering and not by a shared transaction: the payload row
/// commits no later than the first event that references it, which is what
/// keeps one transaction per event.
/// </para>
/// </summary>
/// <remarks>
/// There is deliberately no audit append here. The append takes the chain
/// lock of the trail's monthly partition and holds it until the transaction
/// ends, which would serialize every provider callback against the hub's own
/// ingestion; the provider decides the callback rate and this hub cannot push
/// back on it. The trail of what the feedback meant is written by the
/// consumer that applies it, outside the request.
/// </remarks>
internal sealed class DeliveryEventWriter(
    NotificationsDbContext db,
    IOutboxWriter outboxWriter,
    IEnvelopeCipher cipher,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Key scope of the sealed provider payload. A data class of the module
    /// rather than the owning application: the payload has to be sealed at the
    /// instant of the insert, and which application owns the notification is
    /// not necessarily known then, since the correlation may only resolve
    /// later or never.
    /// </summary>
    internal const string PayloadKeyScope = "notifications-delivery-evidence";

    /// <summary>
    /// Seals the verified provider bytes once per callback and stamps the
    /// instant every event of that callback will carry. Both are per callback
    /// and not per event: the cipher call is the most expensive step of the
    /// request, and a single reception instant is what keeps a batch inside one
    /// monthly partition on both tables instead of straddling two at a month
    /// boundary.
    /// </summary>
    public async Task<SealedDeliveryPayload> SealPayloadAsync(
        string providerKey,
        string source,
        ReadOnlyMemory<byte> verifiedPayload,
        CancellationToken cancellationToken)
        => new(
            timeProvider.GetUtcNow(),
            providerKey,
            source,
            await cipher.EncryptAsync(PayloadKeyScope, verifiedPayload.ToArray(), cancellationToken));

    /// <summary>Records one canonical event, or refuses it as already seen.</summary>
    public async Task<DeliveryEventRecordOutcome> RecordAsync(
        ProviderDeliveryEvent providerEvent,
        DispatchCorrelation? correlation,
        SealedDeliveryPayload payload,
        CancellationToken cancellationToken)
        => (await RecordDiscoveredAsync(providerEvent, correlation, payload, cancellationToken))
            .Outcome;

    /// <summary>
    /// Records one canonical event and names the evidence row it wrote. It is
    /// the same write in every respect, including the queue message: what the
    /// caller gains is the identity of the evidence, which a caller that
    /// applies the event in its own process needs and a caller that only hands
    /// it to the queue does not.
    /// <para>
    /// The queue message is written even when the caller intends to apply the
    /// event itself. It costs one message that is answered as a duplicate on
    /// the ordinary path, and it buys the only cure for a caller that dies
    /// between this commit and its own application: without it the identity
    /// would stay claimed, the evidence would stay unapplied, and no later
    /// round could ever ask about that event again.
    /// </para>
    /// </summary>
    public async Task<DeliveryEventRecorded> RecordDiscoveredAsync(
        ProviderDeliveryEvent providerEvent,
        DispatchCorrelation? correlation,
        SealedDeliveryPayload payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        ArgumentNullException.ThrowIfNull(payload);

        // The callback's own instant, not this event's: the batch was taken
        // once, and one instant is what puts the payload and every event of it
        // in the same monthly partition.
        DateTimeOffset now = payload.ReceivedAt;

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);

        // The ledger is the guard, not a lookup before the insert: two
        // concurrent redeliveries of the same callback would both read absent
        // and both write. Losing the race here is a zero-row insert. Every
        // value travels as a parameter, never as text folded into the command.
        var provider = providerEvent.ProviderKey;
        var providerEventId = providerEvent.ProviderEventId;
        var claimed = await db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO notifications.provider_event_dedupe (provider, provider_event_id, processed_at)
             VALUES ({provider}, {providerEventId}, {now})
             ON CONFLICT (provider, provider_event_id) DO NOTHING
             """,
            cancellationToken);
        if (claimed == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new DeliveryEventRecorded(DeliveryEventRecordOutcome.Duplicate, null);
        }

        // The bytes are written by whichever event first claims its identity,
        // and only then: a batch that is entirely a redelivery claims nothing
        // and must leave nothing behind. The conflict clause covers the round
        // where an earlier transaction wrote the row and then rolled back.
        if (!payload.IsStored)
        {
            await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO notifications.delivery_payload
                     (id, received_at, provider_key, source, payload_enc)
                 VALUES ({payload.Id}, {payload.ReceivedAt}, {payload.ProviderKey},
                         {payload.Source}, {payload.Envelope})
                 ON CONFLICT (id, received_at) DO NOTHING
                 """,
                cancellationToken);
        }

        var evidence = DeliveryEvent.Record(new DeliveryEventDraft
        {
            ReceivedAt = now,
            AttemptId = correlation?.AttemptId,
            NotificationId = correlation?.NotificationId,
            ProviderKey = providerEvent.ProviderKey,
            ProviderEventId = providerEvent.ProviderEventId,
            ProviderMessageId = providerEvent.ProviderMessageId,
            Kind = DeliveryEventKinds.From(providerEvent.Kind),
            OccurredAt = providerEvent.OccurredAt,
            ErrorCode = providerEvent.ErrorCode,

            // Written down, never re-derived: what a failure code says about a
            // destination is provider knowledge, it was already decided on the
            // dispatch side, and the consumer that acts on it runs long after
            // this row is the only thing left of the callback.
            SuppressionSignal = DeliverySuppressionSignals.From(providerEvent.Signal),
            PayloadId = payload.Id,
        });
        db.DeliveryEvents.Add(evidence);
        await db.SaveChangesAsync(cancellationToken);

        await outboxWriter.AppendAsync(
            transaction.GetDbTransaction(),
            DeliveryTrackingMessages.BuildEventReceived(evidence.Id, now, Activity.Current?.Id),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        // After the commit and never before: a rollback has to leave the next
        // event of the batch to write the bytes, or the events that follow
        // would reference a row that never existed.
        payload.MarkStored();
        return new DeliveryEventRecorded(DeliveryEventRecordOutcome.Stored, evidence.Id);
    }
}
