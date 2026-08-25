namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

/// <summary>
/// Where the bytes of one stored payload came from. The distinction is not
/// cosmetic: on the callback path the bytes are the body the provider signed,
/// while on the reconciliation path they are the canonical event this hub
/// serialized after asking. Two different things under one column, and whoever
/// examines the evidence years from now has to know which one they are reading.
/// </summary>
internal static class DeliveryPayloadSources
{
    /// <summary>Body the provider signed and this hub verified octet for octet.</summary>
    internal const string Webhook = "webhook";

    /// <summary>Canonical event this hub serialized from a provider lookup.</summary>
    internal const string Reconciliation = "reconciliation";
}

/// <summary>
/// The verified bytes of one provider callback, sealed once and stored once.
/// Every delivery event the callback carried references this row, because the
/// evidence of an event of a batch is the batch: replicating the batch into
/// each of its own events made the write quadratic in the batch size, which is
/// a variable the provider chooses and this hub cannot push back on.
/// <para>
/// The table is partitioned by month on <see cref="ReceivedAt"/>, aligned with
/// the evidence table. The alignment is structural rather than lucky: the
/// reception instant is stamped once per callback and every event of that
/// callback carries it, so a batch never straddles two partitions and a later
/// retention can retire the two tables on clocks of their own.
/// </para>
/// <para>
/// <see cref="PayloadEncrypted"/> holds the bytes sealed by the envelope cipher
/// under the tracker's own key scope. The raw body carries the destination in
/// the clear, this module forbids personal data at rest in the clear, and this
/// row is now the only place at rest where that body exists at all.
/// </para>
/// </summary>
internal sealed class DeliveryPayload
{
    private DeliveryPayload()
    {
        ProviderKey = null!;
        Source = null!;
        PayloadEncrypted = null!;
    }

    public Guid Id { get; private set; }

    /// <summary>Instant this hub took the callback; the partition column.</summary>
    public DateTimeOffset ReceivedAt { get; private set; }

    public string ProviderKey { get; private set; }

    /// <summary>Durable spelling of what these bytes are; see <see cref="DeliveryPayloadSources"/>.</summary>
    public string Source { get; private set; }

    /// <summary>Envelope-encrypted verified bytes.</summary>
    public byte[] PayloadEncrypted { get; private set; }
}

/// <summary>
/// One sealed callback on its way into the store, shared by every event of the
/// batch. It is a handle and not a value on purpose: the bytes are written by
/// whichever event first claims its identity, and the ones that follow only
/// reference the row that first one wrote. <see cref="IsStored"/> is what tells
/// them apart, and it is set only after the transaction that wrote the row
/// commits, so a rollback leaves the next event to write it.
/// <para>
/// A batch that is entirely a redelivery claims nothing, so nothing is written
/// here either. Writing the payload eagerly before the loop would have been
/// simpler and would have left one orphan row per redelivered callback.
/// </para>
/// </summary>
internal sealed class SealedDeliveryPayload
{
    internal SealedDeliveryPayload(
        DateTimeOffset receivedAt,
        string providerKey,
        string source,
        byte[] envelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (envelope is not { Length: > 0 })
        {
            throw new ArgumentException(
                "A evidência de entrega exige o payload do provedor selado.", nameof(envelope));
        }

        Id = Guid.CreateVersion7();
        ReceivedAt = receivedAt;
        ProviderKey = providerKey;
        Source = source;
        Envelope = envelope;
    }

    public Guid Id { get; }

    /// <summary>Instant of the callback, shared by the payload and every event of it.</summary>
    public DateTimeOffset ReceivedAt { get; }

    public string ProviderKey { get; }

    public string Source { get; }

    /// <summary>The sealed bytes, written once for the whole batch.</summary>
    public byte[] Envelope { get; }

    /// <summary>Whether a committed transaction already wrote the payload row.</summary>
    public bool IsStored { get; private set; }

    /// <summary>
    /// Records that the payload row is committed. Called after the commit and
    /// never before: a transaction that rolls back must leave the next event of
    /// the batch to write the bytes, or the events that follow would reference
    /// a row that never existed.
    /// </summary>
    public void MarkStored() => IsStored = true;
}
