using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class RemoveSuppression
{
    /// <summary>
    /// Takes back the suppression in force over one contact point. The stamp,
    /// the cache-invalidation message and the audit event commit in one
    /// transaction, so a channel never becomes addressable again without the
    /// trail that says who allowed it.
    /// <para>
    /// The row is stamped, never deleted: the question an auditor asks later is
    /// why a message was not sent on a given day, and the answer has to survive
    /// the reversal.
    /// </para>
    /// </summary>
    internal sealed class Handler(
        ContactConsentDbContext db,
        ContactConsentWriter writer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(
            string recipientId,
            Guid contactPointId,
            Command command,
            ContactWriteContext writeContext,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            ArgumentNullException.ThrowIfNull(writeContext);

            ContactPoint? point = await db.ContactPoints
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    candidate => candidate.Id == contactPointId && candidate.RecipientId == recipientId,
                    cancellationToken);
            if (point is null)
            {
                return Result.Success<Outcome>(new Outcome.ContactPointNotFound());
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            ContactSuppression? suppression = await db.Suppressions
                .FirstOrDefaultAsync(
                    candidate => candidate.ContactPointId == contactPointId && candidate.RemovedAt == null,
                    cancellationToken);
            if (suppression is null)
            {
                // Declarative no-op: the channel is already addressable, so no
                // cache event; only the trail of the attempt.
                await writer.AppendStandaloneAuditAsync(
                    writeContext,
                    BuildAuditEntry(
                        writeContext, recipientId, point, command, suppression: null, now),
                    cancellationToken);
                logger.SuppressionRemovalRepeated(recipientId, contactPointId);
                return Result.Success<Outcome>(new Outcome.NotSuppressed());
            }

            suppression.Remove(now, writeContext.ActorId);
            ContactWriteOutcome persisted = await writer.CommitAsync(
                writeContext,
                [ContactConsentEvents.Build(
                    ContactConsentEvents.ContactChanged, recipientId, contactPointId, now)],
                BuildAuditEntry(writeContext, recipientId, point, command, suppression, now),
                cancellationToken);
            if (persisted is ContactWriteOutcome.ConcurrencyConflict)
            {
                return Result.Success<Outcome>(new Outcome.ConcurrencyConflict());
            }

            if (persisted is ContactWriteOutcome.Duplicate)
            {
                throw new InvalidOperationException(
                    "A remoção de supressão não carrega marca de deduplicação; "
                    + "um desfecho duplicado é impossível nesse caminho.");
            }

            logger.SuppressionRemoved(recipientId, contactPointId, writeContext.ActorId);
            return Result.Success<Outcome>(new Outcome.Removed(new Response(
                contactPointId,
                suppression.Channel,
                suppression.Reason,
                suppression.CreatedAt,
                now)));
        }

        private static AuditEntry BuildAuditEntry(
            ContactWriteContext writeContext,
            string recipientId,
            ContactPoint point,
            Command command,
            ContactSuppression? suppression,
            DateTimeOffset now)
            => new()
            {
                ActorType = writeContext.ActorType,
                ActorId = writeContext.ActorId,
                Application = null,
                Action = ContactConsentAuditVocabulary.SuppressionRemoved,
                EntityType = ContactConsentAuditVocabulary.EntityTypeContactPoint,
                EntityId = point.Id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    recipientId,
                    channel = point.Channel,
                    justification = command.Justification,
                    reason = suppression?.Reason,
                    source = suppression?.Source,
                    suppressedAt = suppression?.CreatedAt,
                    alreadyRemoved = suppression is null,
                }),
                OccurredAt = now,
            };
    }
}
