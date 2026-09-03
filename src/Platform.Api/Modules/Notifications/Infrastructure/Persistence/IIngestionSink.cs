using System.Data.Common;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Write seam of the ingestion use case. Accepting is the same write on every
/// transport, so it commits here and now; the trail of an outcome without a
/// business effect is not, because a transport that must record the refusal
/// somewhere else first has to own when that trail commits.
/// </summary>
internal interface IIngestionSink
{
    /// <summary>
    /// Commits one acceptance. <paramref name="attachments"/> is the set the
    /// request asked to be claimed, or nothing when it named none: the claim
    /// belongs inside this write because a notification accepted over a set it
    /// does not hold is the one state nothing downstream can repair.
    /// </summary>
    Task<PersistOutcome> PersistAcceptedAsync(
        Notification notification,
        IdempotencyRegistration registration,
        OutboxAppend outboxMessage,
        AuditEntry auditEntry,
        AttachmentClaimRequest? attachments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Records the trail of a rejection or a duplicate, with the outgoing
    /// integration event when the outcome has one.
    /// </summary>
    Task RecordTrailAsync(
        AuditEntry auditEntry,
        OutboxAppend? integrationEvent,
        CancellationToken cancellationToken);
}

/// <summary>
/// The synchronous posture: the trail commits in its own short transaction as
/// soon as the outcome is known, because the caller is about to receive an
/// answer and nothing else has to happen first.
/// </summary>
internal sealed class CommittedIngestionSink(IngestionWriter writer) : IIngestionSink
{
    public Task<PersistOutcome> PersistAcceptedAsync(
        Notification notification,
        IdempotencyRegistration registration,
        OutboxAppend outboxMessage,
        AuditEntry auditEntry,
        AttachmentClaimRequest? attachments,
        CancellationToken cancellationToken)
        => writer.PersistAcceptedAsync(
            notification, registration, outboxMessage, auditEntry, attachments, cancellationToken);

    public Task RecordTrailAsync(
        AuditEntry auditEntry,
        OutboxAppend? integrationEvent,
        CancellationToken cancellationToken)
        => writer.AppendStandaloneAuditAsync(auditEntry, integrationEvent, cancellationToken);
}

/// <summary>
/// The asynchronous posture: the trail is held until the caller commits it.
/// The bus consumer must record a permanently invalid event on the dead-letter
/// topic before anything marks the offset as handled, so it commits the trail
/// together with the deduplication mark, in that order. Scoped per message on
/// purpose: the buffer belongs to one event.
/// </summary>
internal sealed class DeferredTrailIngestionSink(IngestionWriter writer) : IIngestionSink
{
    private readonly List<(AuditEntry Entry, OutboxAppend? Event)> _pending = [];

    /// <summary>Whether the outcome left a trail for the caller to commit.</summary>
    public bool HasPendingTrail => _pending.Count > 0;

    public Task<PersistOutcome> PersistAcceptedAsync(
        Notification notification,
        IdempotencyRegistration registration,
        OutboxAppend outboxMessage,
        AuditEntry auditEntry,
        AttachmentClaimRequest? attachments,
        CancellationToken cancellationToken)
        => writer.PersistAcceptedAsync(
            notification, registration, outboxMessage, auditEntry, attachments, cancellationToken);

    public Task RecordTrailAsync(
        AuditEntry auditEntry,
        OutboxAppend? integrationEvent,
        CancellationToken cancellationToken)
    {
        _pending.Add((auditEntry, integrationEvent));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes every held trail entry inside the caller's open transaction.
    /// The integration event goes in before the audit append, because the
    /// append holds the partition chain lock until the transaction ends and
    /// anything queued after it widens that window.
    /// </summary>
    public async Task FlushAsync(
        DbTransaction transaction,
        IOutboxWriter outboxWriter,
        IAuditTrail auditTrail,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxWriter);
        ArgumentNullException.ThrowIfNull(auditTrail);

        foreach ((AuditEntry entry, OutboxAppend? integrationEvent) in _pending)
        {
            if (integrationEvent is not null)
            {
                await outboxWriter.AppendAsync(transaction, integrationEvent, cancellationToken);
            }

            await auditTrail.AppendAsync(transaction, entry, cancellationToken);
        }

        _pending.Clear();
    }
}
