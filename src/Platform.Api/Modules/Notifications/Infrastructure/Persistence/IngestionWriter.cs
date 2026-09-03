using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>Outcome of one acceptance persistence attempt.</summary>
internal abstract record PersistOutcome
{
    private PersistOutcome()
    {
    }

    /// <summary>Everything committed together.</summary>
    internal sealed record Accepted : PersistOutcome;

    /// <summary>
    /// The unique idempotency key already existed: someone else won the race
    /// or the fast path missed a replay. The caller compares the stored
    /// payload hash to decide between replay and conflict.
    /// </summary>
    internal sealed record ExistingRegistration(Guid NotificationId, string PayloadHash) : PersistOutcome;

    /// <summary>
    /// The attachments the request named were not claimed, so nothing was
    /// persisted. It is a legitimate outcome and never an error: the set is
    /// claimed whole or the acceptance does not happen, and the caller turns
    /// the refusal into the answer its transport owes the producer.
    /// </summary>
    internal sealed record AttachmentsRefused(AttachmentClaimStatus Status) : PersistOutcome;
}

/// <summary>
/// The transactional invariant of the ingestion: the attachment claim, the
/// notification, the idempotency registration, the outbox message and the
/// audit event commit in one database transaction or not at all. The claim,
/// the outbox and the audit contracts all receive the raw transaction and run
/// their own statements on its connection, and the commit follows the audit
/// append immediately because the append holds the partition chain lock until
/// the transaction ends.
/// </summary>
/// <remarks>
/// <para>
/// The order inside the transaction is the decided one and it is not a style
/// choice. The claim goes first, so that a set that cannot be claimed costs
/// nothing else; the notification and its key follow; the outbox message is
/// queued next; and the audit append is last because it takes the chain lock
/// and holds it until the transaction ends, so anything queued after it widens
/// the window every concurrent ingestion waits on.
/// </para>
/// <para>
/// The transaction declares READ COMMITTED rather than accepting whatever the
/// server, the database or the role happens to default to. Every statement of
/// a READ COMMITTED transaction takes a fresh snapshot, which is what lets the
/// audit append read a chain tail that is final once it holds the lock, and
/// what lets the claim read a dependency row committed by the transaction it
/// just waited on. A stronger level takes one snapshot for the whole
/// transaction, before either lock is granted, and both reads go stale.
/// </para>
/// <para>
/// Losing the race on the idempotency key rolls the whole unit back and
/// disposes it before the winning registration is read. It matters because
/// that read would otherwise run inside a transaction that still holds the
/// claim and every row lock the claim took, and the row locks are the ones
/// every other acceptance of the same attachments is waiting on.
/// </para>
/// </remarks>
internal sealed class IngestionWriter(
    NotificationsDbContext db,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail,
    IAttachmentClaim attachmentClaim)
{
    private const string ReadCommitted = "read committed";

    private const string EffectiveIsolationSql = "SELECT current_setting('transaction_isolation')";

    // Whether this session is outside a transaction that wrote anything. A
    // transaction that has written carries an assigned identifier; one that
    // never began, and one that ended, carry none.
    private const string AssignedTransactionSql = """
        SELECT pg_current_xact_id_if_assigned() IS NOT NULL AS "Value"
        """;

    public async Task<PersistOutcome> PersistAcceptedAsync(
        Notification notification,
        IdempotencyRegistration registration,
        OutboxAppend outboxMessage,
        AuditEntry auditEntry,
        AttachmentClaimRequest? attachments,
        CancellationToken cancellationToken)
    {
        await using (IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken))
        {
            DbTransaction shared = transaction.GetDbTransaction();
            try
            {
                if (attachments is not null)
                {
                    await RefuseUnlessReadCommittedAsync(shared, cancellationToken);
                    AttachmentClaimOutcome claim = await attachmentClaim.ClaimAsync(
                        shared, attachments, cancellationToken);
                    if (claim.Status != AttachmentClaimStatus.Claimed)
                    {
                        // Leaving the block rolls the transaction back, and
                        // the claim wrote nothing anyway: it accepts the whole
                        // set or it changes nothing.
                        return new PersistOutcome.AttachmentsRefused(claim.Status);
                    }

                    // The snapshot is frozen here, onto an entity nothing has
                    // stored yet, so it travels in the insert below and never
                    // in a statement of its own. This is the only point where
                    // the accepted set and the un-inserted notification exist
                    // together: the claim runs inside this transaction, and
                    // the notification was built before the transaction was
                    // opened, by a use case that owns no transaction at all.
                    // Handing the set back to that use case instead would put
                    // the document in an update after the insert, and moving
                    // the claim to it would put the claim outside the
                    // transaction the acceptance commits in.
                    notification.FreezeAcceptedAttachments(
                        AcceptedAttachmentManifest.Serialize(AcceptedOf(claim)));
                }

                db.Notifications.Add(notification);
                db.IdempotencyRegistrations.Add(registration);
                await db.SaveChangesAsync(cancellationToken);
                await outboxWriter.AppendAsync(shared, outboxMessage, cancellationToken);
                await auditTrail.AppendAsync(shared, auditEntry, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new PersistOutcome.Accepted();
            }
            catch (DbUpdateException exception)
                when (exception.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.UniqueViolation,
                } violation && IsIdempotencyConstraint(violation))
            {
                db.Notifications.Remove(notification);
                db.IdempotencyRegistrations.Remove(registration);

                // The save failed inside a savepoint, so the transaction is
                // still alive and still holds the claim and every row lock the
                // claim took. The rollback here, and the disposal at the end
                // of this block, are what the read below runs without.
                await transaction.RollbackAsync(cancellationToken);
            }
        }

        return await ReadWinningRegistrationAsync(registration, cancellationToken);
    }

    /// <summary>The registration that answers a replay, straight from the authority table.</summary>
    public async Task<IdempotencyRegistration?> FindRegistrationAsync(
        string application,
        string idempotencyKey,
        CancellationToken cancellationToken)
        => await db.IdempotencyRegistrations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                registration => registration.Application == application
                    && registration.IdempotencyKey == idempotencyKey,
                cancellationToken);

    /// <summary>
    /// Records one ingress audit event in its own short transaction, together
    /// with the outgoing integration event when the outcome has one: used for
    /// rejections and duplicates, which have no business effect to share a
    /// transaction with but still must leave a trail.
    /// </summary>
    /// <remarks>
    /// The outbox append runs before the audit append on purpose. The audit
    /// append takes the partition chain lock and holds it until the
    /// transaction ends, so anything queued after it extends the window every
    /// concurrent ingestion waits on. Order here is a latency decision, not a
    /// style choice.
    /// </remarks>
    public async Task AppendStandaloneAuditAsync(
        AuditEntry auditEntry,
        OutboxAppend? integrationEvent,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        if (integrationEvent is not null)
        {
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(), integrationEvent, cancellationToken);
        }

        await auditTrail.AppendAsync(transaction.GetDbTransaction(), auditEntry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the registration that won the race, and refuses to read it from
    /// inside a transaction that is still open.
    /// <para>
    /// The refusal is not defensive decoration. The losing unit reaches this
    /// point holding a claim over every attachment of the request and a row
    /// lock over every one of them, and reading the winner without letting go
    /// of either would make every concurrent acceptance of those attachments
    /// wait on a query that has nothing to do with them. The server is asked
    /// rather than the context, because what has to be true is that this
    /// session is outside a transaction, not that one object believes so.
    /// </para>
    /// </summary>
    private async Task<PersistOutcome> ReadWinningRegistrationAsync(
        IdempotencyRegistration registration,
        CancellationToken cancellationToken)
    {
        List<bool> assigned = await db.Database
            .SqlQueryRaw<bool>(AssignedTransactionSql)
            .ToListAsync(cancellationToken);
        if (assigned is [true])
        {
            throw new InvalidOperationException(
                "A unidade perdedora ainda está em transação aberta: o registro vencedor não pode "
                    + "ser consultado enquanto o claim e os bloqueios de linha do perdedor existirem.");
        }

        IdempotencyRegistration existing =
            await FindRegistrationAsync(registration.Application, registration.IdempotencyKey, cancellationToken)
            ?? throw new InvalidOperationException(
                "A chave de idempotência violou a restrição única, mas o registro vencedor não foi encontrado.");
        return new PersistOutcome.ExistingRegistration(existing.NotificationId, existing.PayloadHash);
    }

    /// <summary>
    /// Refuses to claim under any level but READ COMMITTED, asking the server
    /// what the running transaction is rather than what the caller declared.
    /// A server, database or role default can set the level without any caller
    /// mentioning it, and a stronger one takes its snapshot before the row
    /// locks of the claim are granted, so the claim would read the state of
    /// the set as it stood before the transaction it waited on.
    /// </summary>
    private static async Task RefuseUnlessReadCommittedAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        DbConnection connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "A transação de aceite não tem conexão aberta.");
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = EffectiveIsolationSql;
        var isolation = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(isolation, ReadCommitted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"O servidor informa nível de isolamento '{isolation}' para a transação de aceite e o "
                    + "claim exige READ COMMITTED: um nível mais forte tira o snapshot antes dos "
                    + "bloqueios de linha do claim.");
        }
    }

    /// <summary>
    /// The set a claim reports as accepted. An outcome that says the set was
    /// claimed always carries it, because the only way to build one is a
    /// factory that refuses a missing set; the refusal here exists so that an
    /// implementation which found a way around that stops the acceptance
    /// instead of inserting a notification with no snapshot on it.
    /// </summary>
    private static AcceptedAttachmentSet AcceptedOf(AttachmentClaimOutcome claim)
        => claim.Accepted
            ?? throw new InvalidOperationException(
                "O claim informou o conjunto de anexos como aceito e não devolveu o "
                    + "snapshot dele.");

    private static bool IsIdempotencyConstraint(PostgresException violation)
        => violation.ConstraintName is null
            || violation.ConstraintName.Contains("idempotency", StringComparison.OrdinalIgnoreCase);
}
