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
/// commits three writes together or none: the deduplication mark that claims
/// the provider's event identity, the evidence row carrying the sealed
/// provider payload, and the queue message that hands the event to its
/// asynchronous application.
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
    /// Seals the verified provider bytes once per callback. A batch shares one
    /// ciphertext across its rows on purpose: the evidence of every event in a
    /// batch is the batch itself, and sealing once keeps the cryptographic
    /// work off the per-event path of the latency budget.
    /// </summary>
    public async Task<byte[]> SealPayloadAsync(
        ReadOnlyMemory<byte> verifiedPayload,
        CancellationToken cancellationToken)
        => await cipher.EncryptAsync(PayloadKeyScope, verifiedPayload.ToArray(), cancellationToken);

    /// <summary>Records one canonical event, or refuses it as already seen.</summary>
    public async Task<DeliveryEventRecordOutcome> RecordAsync(
        ProviderDeliveryEvent providerEvent,
        DispatchCorrelation? correlation,
        byte[] sealedPayload,
        CancellationToken cancellationToken)
        => (await RecordDiscoveredAsync(providerEvent, correlation, sealedPayload, cancellationToken))
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
        byte[] sealedPayload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(providerEvent);
        DateTimeOffset now = timeProvider.GetUtcNow();

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
            PayloadEncrypted = sealedPayload,
        });
        db.DeliveryEvents.Add(evidence);
        await db.SaveChangesAsync(cancellationToken);

        await outboxWriter.AppendAsync(
            transaction.GetDbTransaction(),
            DeliveryTrackingMessages.BuildEventReceived(evidence.Id, now, Activity.Current?.Id),
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new DeliveryEventRecorded(DeliveryEventRecordOutcome.Stored, evidence.Id);
    }
}
