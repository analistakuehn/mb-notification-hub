using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
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
    /// </summary>
    internal sealed class Handler(
        ContactConsentDbContext db,
        ContactConsentWriter writer,
        TimeProvider timeProvider,
        ILogger<Handler> logger)
    {
        public async Task<Result<Outcome>> HandleAsync(
            string recipientId,
            Command command,
            string actorId,
            string actorType,
            CancellationToken cancellationToken)
        {
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
                .GroupBy(consent => (consent.Purpose, channelByPointId[consent.ContactPointId]))
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

                if (inForce.TryGetValue((declaration.Purpose, declaration.Channel), out Consent? current)
                    && current.Granted == declaration.Granted)
                {
                    continue;
                }

                var consent = Consent.Record(
                    anchor.Id,
                    declaration.Purpose,
                    declaration.Granted,
                    declaration.Source,
                    actorId,
                    declaration.TermsVersion,
                    now);
                db.Consents.Add(consent);
                recorded.Add(consent);
                inForce[(declaration.Purpose, declaration.Channel)] = consent;
                messages.Add(ContactConsentEvents.Build(
                    ContactConsentEvents.ConsentChanged, recipientId, anchor.Id, now));
            }

            AuditEntry auditEntry = BuildAuditEntry(
                recipientId, actorId, actorType, now, command.Consents.Count, recorded);
            if (recorded.Count == 0)
            {
                await writer.AppendStandaloneAuditAsync(auditEntry, cancellationToken);
                logger.ConsentsUnchanged(recipientId);
                return Result.Success<Outcome>(
                    new Outcome.Declared(BuildResponse(recipientId, inForce, channelByPointId)));
            }

            ContactWriteOutcome persisted = await writer.CommitAsync(messages, auditEntry, cancellationToken);
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
            string actorId,
            string actorType,
            DateTimeOffset occurredAt,
            int declaredCount,
            List<Consent> recorded) => new()
        {
            ActorType = actorType,
            ActorId = actorId,
            Application = null,
            Action = ContactConsentAuditVocabulary.ConsentsDeclared,
            EntityType = ContactConsentAuditVocabulary.EntityTypeRecipient,
            EntityId = recipientId,
            DetailsJson = JsonSerializer.Serialize(new
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
            }),
            OccurredAt = occurredAt,
        };

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
