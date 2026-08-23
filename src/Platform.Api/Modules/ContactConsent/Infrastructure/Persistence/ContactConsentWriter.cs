using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Infrastructure.Messaging;
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
}

/// <summary>
/// The transactional invariant of every write of this module: the tracked
/// entity changes, the cache-invalidation outbox messages and the audit event
/// commit in one database transaction or not at all. The outbox and audit
/// contracts receive the raw transaction, and the commit follows the audit
/// append immediately because the append holds the partition chain lock until
/// the transaction ends.
/// </summary>
internal sealed class ContactConsentWriter(
    ContactConsentDbContext db,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail)
{
    public async Task<ContactWriteOutcome> CommitAsync(
        IReadOnlyList<OutboxAppend> outboxMessages,
        AuditEntry auditEntry,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
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
    /// to share a transaction with but still must leave a trail.
    /// </summary>
    public async Task AppendStandaloneAuditAsync(AuditEntry auditEntry, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        await auditTrail.AppendAsync(transaction.GetDbTransaction(), auditEntry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}
