using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using NotificationHub.Api.Infrastructure.Messaging;
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
}

/// <summary>
/// The transactional invariant of the ingestion: notification, idempotency
/// registration, outbox message and audit event commit in one database
/// transaction or not at all. The outbox and audit contracts receive the raw
/// transaction, and the commit follows the audit append immediately because
/// the append holds the partition chain lock until the transaction ends.
/// </summary>
internal sealed class IngestionWriter(
    NotificationsDbContext db,
    IOutboxWriter outboxWriter,
    IAuditTrail auditTrail)
{
    public async Task<PersistOutcome> PersistAcceptedAsync(
        Notification notification,
        IdempotencyRegistration registration,
        OutboxAppend outboxMessage,
        AuditEntry auditEntry,
        CancellationToken cancellationToken)
    {
        db.Notifications.Add(notification);
        db.IdempotencyRegistrations.Add(registration);

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await outboxWriter.AppendAsync(transaction.GetDbTransaction(), outboxMessage, cancellationToken);
            await auditTrail.AppendAsync(transaction.GetDbTransaction(), auditEntry, cancellationToken);
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
            IdempotencyRegistration existing =
                await FindRegistrationAsync(registration.Application, registration.IdempotencyKey, cancellationToken)
                ?? throw new InvalidOperationException(
                    "A chave de idempotência violou a restrição única, mas o registro vencedor não foi encontrado.");
            return new PersistOutcome.ExistingRegistration(existing.NotificationId, existing.PayloadHash);
        }
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
            await db.Database.BeginTransactionAsync(cancellationToken);
        if (integrationEvent is not null)
        {
            await outboxWriter.AppendAsync(
                transaction.GetDbTransaction(), integrationEvent, cancellationToken);
        }

        await auditTrail.AppendAsync(transaction.GetDbTransaction(), auditEntry, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static bool IsIdempotencyConstraint(PostgresException violation)
        => violation.ConstraintName is null
            || violation.ConstraintName.Contains("idempotency", StringComparison.OrdinalIgnoreCase);
}
