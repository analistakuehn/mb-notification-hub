using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareConsents
{
    /// <summary>
    /// Declarative reconciliation over the append-only ledger: each declared
    /// (purpose, channel) state is compared with the latest record in force;
    /// only a difference appends a new grant or revocation record, anchored on
    /// the newest active contact point of the channel, with origin, actor,
    /// terms version and instant. Nothing is ever updated or deleted. The
    /// first declaration of a pair always records, even a revocation, so an
    /// imported opt-out leaves an explicit ledger baseline.
    ///
    /// The pair is keyed on the canonical purpose on both sides of the
    /// comparison. Records written before the aggregate canonicalized are
    /// folded into the same lineage by that key, which is the only repair the
    /// ledger admits: the table rejects UPDATE, and rewriting a declaration
    /// somebody actually made is not a repair anyway. So the resolution reads
    /// every spelling and the write appends exactly one.
    ///
    /// Every record actually appended is announced twice in the transaction
    /// that appends it: once to the internal invalidation queue, so the hub's
    /// own caches stop serving a stance that changed, and once to the outgoing
    /// topic of the corporate bus, so the domains learn it. A declaration that
    /// changed nothing announces nothing.
    /// </summary>
    internal sealed class Handler(
        ContactConsentDbContext db,
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

            var profileExists = await db.RecipientProfiles
                .AsNoTracking()
                .AnyAsync(profile => profile.RecipientId == recipientId, cancellationToken);
            if (!profileExists)
            {
                return Result.Success<Outcome>(new Outcome.RecipientUnknown());
            }

            List<ContactPoint> points = await db.ContactPoints
                .AsNoTracking()
                .Where(point => point.RecipientId == recipientId)
                .ToListAsync(cancellationToken);
            var pointIds = points.Select(point => point.Id).ToList();
            var channelByPointId = points.ToDictionary(point => point.Id, point => point.Channel);

            List<Consent> ledger = await db.Consents
                .AsNoTracking()
                .Where(consent => pointIds.Contains(consent.ContactPointId))
                .ToListAsync(cancellationToken);
            Dictionary<(string Purpose, string Channel), Consent> inForce = ledger
                .GroupBy(consent => (
                    ConsentPurpose.Canonicalize(consent.Purpose),
                    channelByPointId[consent.ContactPointId]))
                .ToDictionary(
                    group => group.Key,
                    group => group
                        .OrderByDescending(consent => consent.RecordedAt)
                        .ThenByDescending(consent => consent.Id)
                        .First());

            DateTimeOffset now = timeProvider.GetUtcNow();
            List<Consent> recorded = [];
            List<OutboxAppend> messages = [];
            foreach (ConsentDeclaration declaration in command.Consents)
            {
                ContactPoint? anchor = points
                    .Where(point => point.IsActive && point.Channel == declaration.Channel)
                    .OrderByDescending(point => point.Id)
                    .FirstOrDefault();
                if (anchor is null)
                {
                    return Result.Success<Outcome>(new Outcome.NoContactPointForChannel(declaration.Channel));
                }

                var purpose = ConsentPurpose.Canonicalize(declaration.Purpose);
                if (inForce.TryGetValue((purpose, declaration.Channel), out Consent? current)
                    && current.Granted == declaration.Granted)
                {
                    continue;
                }

                var consent = Consent.Record(
                    anchor.Id,
                    declaration.Purpose,
                    declaration.Granted,
                    declaration.Source,
                    writeContext.ActorId,
                    declaration.TermsVersion,
                    now);
                db.Consents.Add(consent);
                recorded.Add(consent);
                inForce[(purpose, declaration.Channel)] = consent;
                messages.Add(ContactConsentEvents.Build(
                    ContactConsentEvents.ConsentChanged, recipientId, anchor.Id, now));

                // The announcement carries the key the domains must correlate
                // on, not the spelling the declaration happened to use.
                messages.Add(ContactConsentEvents.BuildConsentChanged(new ConsentChangedFact
                {
                    RecipientId = recipientId,
                    Channel = declaration.Channel,
                    Purpose = consent.Purpose,
                    Granted = declaration.Granted,
                    Source = declaration.Source,
                    OccurredAt = now,
                }));
            }

            AuditEntry auditEntry = BuildAuditEntry(
                recipientId, writeContext, now, command.Consents.Count, recorded);
            if (recorded.Count == 0)
            {
                ContactWriteOutcome noOp = await writer.AppendStandaloneAuditAsync(
                    writeContext, auditEntry, cancellationToken);
                if (noOp is ContactWriteOutcome.Duplicate)
                {
                    return Result.Success<Outcome>(new Outcome.Duplicate());
                }

                logger.ConsentsUnchanged(recipientId);
                return Result.Success<Outcome>(
                    new Outcome.Declared(BuildResponse(recipientId, inForce, channelByPointId)));
            }

            ContactWriteOutcome persisted = await writer.CommitAsync(
                writeContext, messages, auditEntry, cancellationToken);
            if (persisted is ContactWriteOutcome.Duplicate)
            {
                return Result.Success<Outcome>(new Outcome.Duplicate());
            }

            if (persisted is ContactWriteOutcome.ConcurrencyConflict)
            {
                logger.ConsentWriteConflict(recipientId);
                return Result.Success<Outcome>(new Outcome.ConcurrencyConflict());
            }

            logger.ConsentsDeclared(recipientId, recorded.Count);
            return Result.Success<Outcome>(
                new Outcome.Declared(BuildResponse(recipientId, inForce, channelByPointId)));
        }

        private static AuditEntry BuildAuditEntry(
            string recipientId,
            ContactWriteContext writeContext,
            DateTimeOffset occurredAt,
            int declaredCount,
            List<Consent> recorded)
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
                Action = ContactConsentAuditVocabulary.ConsentsDeclared,
                EntityType = ContactConsentAuditVocabulary.EntityTypeRecipient,
                EntityId = recipientId,
                DetailsJson = JsonSerializer.Serialize(
                    new
                    {
                        declared = declaredCount,
                        changed = recorded.Count,
                        records = recorded.Select(consent => new
                        {
                            consentId = consent.Id,
                            contactPointId = consent.ContactPointId,
                            purpose = consent.Purpose,
                            granted = consent.Granted,
                            source = consent.Source,
                            termsVersion = consent.TermsVersion,
                        }),
                        origin,
                    },
                    DetailsOptions),
                OccurredAt = occurredAt,
            };
        }

        private static Response BuildResponse(
            string recipientId,
            Dictionary<(string Purpose, string Channel), Consent> inForce,
            Dictionary<Guid, string> channelByPointId)
            => new(
                recipientId,
                inForce
                    .OrderBy(entry => entry.Key.Purpose, StringComparer.Ordinal)
                    .ThenBy(entry => entry.Key.Channel, StringComparer.Ordinal)
                    .Select(entry => new ConsentItem(
                        entry.Key.Purpose,
                        channelByPointId[entry.Value.ContactPointId],
                        entry.Value.Granted,
                        entry.Value.Source,
                        entry.Value.TermsVersion,
                        entry.Value.RecordedAt))
                    .ToList());
    }
}
