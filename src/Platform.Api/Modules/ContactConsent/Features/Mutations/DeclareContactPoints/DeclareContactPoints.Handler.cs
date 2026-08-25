using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareContactPoints
{
    /// <summary>
    /// Reconciles the declared contact points against the stored truth. A new
    /// (channel, value) pair becomes a new row; a declared pair that already
    /// exists revives it when removed and applies the verified flag; an active
    /// pair absent from the declaration is stamped removed, never deleted,
    /// because the consent ledger anchors on it. Profile preferences apply
    /// when present. All changes, the invalidation messages and the audit
    /// event commit in one transaction.
    ///
    /// The transport is not a parameter of the reconciliation: the REST route
    /// and the bus ingress hand the same command to this handler and differ
    /// only in the write context they build.
    /// </summary>
    internal sealed class Handler(
        ContactConsentDbContext db,
        ContactValueProtector protector,
        ContactConsentWriter writer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        /// <summary>
        /// Omitting nulls keeps the evidence of a write without provenance
        /// exactly as it was before the bus path existed; every other field of
        /// this document is always present.
        /// </summary>
        private static readonly JsonSerializerOptions DetailsOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        public async Task<Result<Outcome>> HandleAsync(
            string recipientId,
            Command command,
            ContactWriteContext writeContext,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(writeContext);

            DateTimeOffset now = timeProvider.GetUtcNow();
            var declarations = command.ContactPoints
                .Select(point =>
                {
                    var normalized = ContactValue.Normalize(point.Channel, point.Value);
                    return (point.Channel, Normalized: normalized,
                        Hash: protector.Hash(normalized), point.Verified);
                })
                .ToList();

            RecipientProfile? profile = await db.RecipientProfiles
                .FirstOrDefaultAsync(candidate => candidate.RecipientId == recipientId, cancellationToken);
            List<ContactPoint> storedPoints = await db.ContactPoints
                .Where(point => point.RecipientId == recipientId)
                .ToListAsync(cancellationToken);

            var profileChanged = false;
            if (profile is null)
            {
                profile = RecipientProfile.Create(recipientId, command.Timezone, command.Locale, now);
                db.RecipientProfiles.Add(profile);
                profileChanged = true;
            }
            else
            {
                profileChanged = profile.ApplyPreferences(command.Timezone, command.Locale, now);
            }

            var added = 0;
            var updated = 0;
            List<Guid> affectedPointIds = [];
            foreach ((var channel, var normalized, var hash, var verified) in declarations)
            {
                ContactPoint? existing = storedPoints
                    .FirstOrDefault(point => point.Channel == channel && point.ValueHash == hash);
                if (existing is null)
                {
                    ProtectedContactValue protectedValue =
                        await protector.ProtectAsync(normalized, cancellationToken);
                    var created = ContactPoint.Declare(
                        recipientId, channel, protectedValue.Encrypted, hash, verified);
                    db.ContactPoints.Add(created);
                    storedPoints.Add(created);
                    affectedPointIds.Add(created.Id);
                    added++;
                }
                else
                {
                    var revived = existing.Restore();
                    var verifiedChanged = existing.ApplyVerified(verified);
                    if (revived || verifiedChanged)
                    {
                        affectedPointIds.Add(existing.Id);
                        updated++;
                    }
                }
            }

            var removed = 0;
            var declaredKeys = declarations
                .Select(entry => (entry.Channel, entry.Hash))
                .ToHashSet();
            foreach (ContactPoint point in storedPoints)
            {
                if (point.IsActive && !declaredKeys.Contains((point.Channel, point.ValueHash)))
                {
                    point.Remove(now);
                    affectedPointIds.Add(point.Id);
                    removed++;
                }
            }

            var summary = new DeclarationSummary(added, updated, removed, profileChanged, affectedPointIds);
            AuditEntry auditEntry = BuildAuditEntry(recipientId, writeContext, now, summary);
            if (affectedPointIds.Count == 0 && !profileChanged)
            {
                ContactWriteOutcome recorded = await writer.AppendStandaloneAuditAsync(
                    writeContext, auditEntry, cancellationToken);
                if (recorded is ContactWriteOutcome.Duplicate)
                {
                    return Result.Success<Outcome>(new Outcome.Duplicate());
                }

                logger.ContactPointsUnchanged(recipientId);
                return Result.Success<Outcome>(new Outcome.Declared(BuildResponse(profile, storedPoints)));
            }

            var messages = affectedPointIds
                .Select(pointId => ContactConsentEvents.Build(
                    ContactConsentEvents.ContactChanged, recipientId, pointId, now))
                .ToList();
            if (messages.Count == 0)
            {
                messages.Add(ContactConsentEvents.Build(
                    ContactConsentEvents.ContactChanged, recipientId, null, now));
            }

            ContactWriteOutcome persisted = await writer.CommitAsync(
                writeContext, messages, auditEntry, cancellationToken);
            if (persisted is ContactWriteOutcome.Duplicate)
            {
                return Result.Success<Outcome>(new Outcome.Duplicate());
            }

            if (persisted is ContactWriteOutcome.ConcurrencyConflict)
            {
                logger.ContactWriteConflict(recipientId);
                return Result.Success<Outcome>(new Outcome.ConcurrencyConflict());
            }

            logger.ContactPointsDeclared(recipientId, added, updated, removed);
            return Result.Success<Outcome>(new Outcome.Declared(BuildResponse(profile, storedPoints)));
        }

        /// <summary>What one declaration changed; the audit evidence, free of contact data.</summary>
        private sealed record DeclarationSummary(
            int Added,
            int Updated,
            int Removed,
            bool ProfileChanged,
            IReadOnlyList<Guid> AffectedPointIds);

        private static AuditEntry BuildAuditEntry(
            string recipientId,
            ContactWriteContext writeContext,
            DateTimeOffset occurredAt,
            DeclarationSummary summary)
        {
            // Present only for a write that arrived as a record: the
            // coordinates let a disputed declaration be checked against what
            // the broker still holds, and the event id correlates it with the
            // producer's own log.
            object? origin = writeContext.Provenance is { } provenance
                ? new { record = provenance.RecordId, eventId = provenance.EventId }
                : null;

            return new AuditEntry
            {
                ActorType = writeContext.ActorType,
                ActorId = writeContext.ActorId,
                Application = null,
                Action = ContactConsentAuditVocabulary.ContactPointsDeclared,
                EntityType = ContactConsentAuditVocabulary.EntityTypeRecipient,
                EntityId = recipientId,
                DetailsJson = JsonSerializer.Serialize(
                    new
                    {
                        added = summary.Added,
                        updated = summary.Updated,
                        removed = summary.Removed,
                        profileChanged = summary.ProfileChanged,
                        contactPointIds = summary.AffectedPointIds,
                        origin,
                    },
                    DetailsOptions),
                OccurredAt = occurredAt,
            };
        }

        private static Response BuildResponse(RecipientProfile profile, IReadOnlyList<ContactPoint> points)
            => new(
                profile.RecipientId,
                profile.EffectiveTimezone,
                profile.Locale,
                points
                    .Where(point => point.IsActive)
                    .OrderBy(point => point.Channel, StringComparer.Ordinal)
                    .ThenBy(point => point.Id)
                    .Select(point => new ContactPointItem(point.Id, point.Channel, point.Verified))
                    .ToList());
    }
}
