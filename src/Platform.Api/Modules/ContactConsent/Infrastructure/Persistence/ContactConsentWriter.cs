using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;

/// <summary>Outcome of one transactional write attempt.</summary>
internal enum ContactWriteOutcome
{
    /// <summary>Everything committed together.</summary>
    Committed = 0,

    /// <summary>
    /// A unique constraint rejected the write: a concurrent declaration for
    /// the same recipient won the race. The caller answers a retryable
    /// conflict instead of a server error.
    /// </summary>
    ConcurrencyConflict = 1,

    /// <summary>
    /// The record that carried this write was already settled: its
    /// deduplication mark existed, so nothing was written a second time.
    /// Unreachable on the REST path, which carries no provenance.
    /// </summary>
    Duplicate = 2,
}

/// <summary>
/// The transactional invariant of every write of this module: the tracked
/// entity changes, the outbox messages and the audit event commit in one
/// database transaction or not at all. The outbox and audit contracts receive
/// the raw transaction, and the commit follows the audit append immediately
/// because the append holds the partition chain lock until the transaction
/// ends.
///
/// A write that arrived as a consumed record stamps its deduplication mark in
/// that same transaction. There is no second guard behind it here: unlike the
/// notification ingestion, a contact declaration carries no unique business
/// key, so the mark is the only thing that keeps a redelivery after a
/// rebalance from writing the trail of an effect that already happened.
/// </summary>
internal sealed class ContactConsentWriter(
    ContactConsentDbContext db,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail,
    IProcessedMessageStore processedMessages)
{
    public async Task<ContactWriteOutcome> CommitAsync(
        ContactWriteContext context,
        IReadOnlyList<OutboxAppend> outboxMessages,
        AuditEntry auditEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // The mark goes first: when it already exists the record was fully
            // settled before, the transaction rolls back and nothing is
            // written twice.
            if (!await TryMarkAsync(transaction, context.Provenance, cancellationToken))
            {
                db.ChangeTracker.Clear();
                return ContactWriteOutcome.Duplicate;
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (OutboxAppend message in outboxMessages)
            {
                await outboxWriter.AppendAsync(transaction.GetDbTransaction(), message, cancellationToken);
            }

            await auditTrail.AppendAsync(transaction.GetDbTransaction(), auditEntry, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return ContactWriteOutcome.Committed;
        }
        catch (DbUpdateException exception)
            when (exception.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            })
        {
            db.ChangeTracker.Clear();
            return ContactWriteOutcome.ConcurrencyConflict;
        }
    }

    /// <summary>
    /// Records the audit event of a write that produced no state change, in
    /// its own short transaction: a declarative no-op has no business effect
    /// to share a transaction with but still must leave a trail. The
    /// deduplication mark commits with that trail, otherwise a rebalance would
    /// fill the hash-chained trail with entries of an event already settled.
    /// </summary>
    public async Task<ContactWriteOutcome> AppendStandaloneAuditAsync(
        ContactWriteContext context,
        AuditEntry auditEntry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await TryMarkAsync(transaction, context.Provenance, cancellationToken))
        {
            return ContactWriteOutcome.Duplicate;
        }

        await auditTrail.AppendAsync(transaction.GetDbTransaction(), auditEntry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ContactWriteOutcome.Committed;
    }

    /// <summary>
    /// Commits only the deduplication mark of a consumed record, for a refusal
    /// that leaves neither state nor trail behind. The dead-letter record
    /// already exists when this runs: a mark written before it would make the
    /// replay of a crash skip a record nobody ever recorded. Returns false
    /// when the mark already existed.
    /// </summary>
    public async Task<bool> TryMarkProcessedAsync(
        ContactWriteProvenance provenance,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provenance);

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        var marked = await processedMessages.TryMarkAsync(
            transaction.GetDbTransaction(), provenance.RecordId, provenance.Consumer, cancellationToken);
        if (!marked)
        {
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>A write without provenance has no record to deduplicate; it always proceeds.</summary>
    private async Task<bool> TryMarkAsync(
        IDbContextTransaction transaction,
        ContactWriteProvenance? provenance,
        CancellationToken cancellationToken)
        => provenance is null
            || await processedMessages.TryMarkAsync(
                transaction.GetDbTransaction(), provenance.RecordId, provenance.Consumer, cancellationToken);
}
