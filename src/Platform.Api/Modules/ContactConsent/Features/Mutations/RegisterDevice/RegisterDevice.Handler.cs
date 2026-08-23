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

internal static partial class RegisterDevice
{
    /// <summary>
    /// Registers one push token for the recipient, creating the profile row on
    /// first contact with the module. A re-registration of the same token
    /// refreshes the last-seen instant and the app version without duplicating
    /// the row and without an invalidation event, because the active token set
    /// did not change. Registration, invalidation message and audit event
    /// commit in one transaction.
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
            ContactWriteContext writeContext,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(writeContext);

            DateTimeOffset now = timeProvider.GetUtcNow();
            RecipientProfile? profile = await db.RecipientProfiles
                .FirstOrDefaultAsync(candidate => candidate.RecipientId == recipientId, cancellationToken);
            var profileCreated = false;
            if (profile is null)
            {
                db.RecipientProfiles.Add(RecipientProfile.Create(recipientId, null, null, now));
                profileCreated = true;
            }

            DeviceToken? device = await db.DeviceTokens
                .FirstOrDefaultAsync(
                    candidate => candidate.RecipientId == recipientId && candidate.Token == command.Token,
                    cancellationToken);
            var isNew = device is null;
            if (device is null)
            {
                device = DeviceToken.Register(recipientId, command.Token, command.Platform, command.AppVersion, now);
                db.DeviceTokens.Add(device);
            }
            else
            {
                device.Touch(command.AppVersion, now);
            }

            List<OutboxAppend> messages = isNew
                ? [ContactConsentEvents.Build(ContactConsentEvents.ContactChanged, recipientId, null, now)]
                : [];
            AuditEntry auditEntry = new()
            {
                ActorType = writeContext.ActorType,
                ActorId = writeContext.ActorId,
                Application = null,
                Action = ContactConsentAuditVocabulary.DeviceRegistered,
                EntityType = ContactConsentAuditVocabulary.EntityTypeDeviceToken,
                EntityId = device.Id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    recipientId,
                    platform = device.Platform,
                    appVersion = device.AppVersion,
                    reRegistration = !isNew,
                    profileCreated,
                }),
                OccurredAt = now,
            };

            ContactWriteOutcome persisted = await writer.CommitAsync(
                writeContext, messages, auditEntry, cancellationToken);
            if (persisted is ContactWriteOutcome.ConcurrencyConflict)
            {
                logger.DeviceWriteConflict(recipientId);
                return Result.Success<Outcome>(new Outcome.ConcurrencyConflict());
            }

            if (persisted is ContactWriteOutcome.Duplicate)
            {
                // Device registration is a REST-only route: its write carries
                // no record to deduplicate, so a duplicate here would mean the
                // caller invented a provenance this use case cannot answer.
                throw new InvalidOperationException(
                    "O registro de dispositivo não carrega marca de deduplicação; "
                    + "um desfecho duplicado é impossível nesse caminho.");
            }

            logger.DeviceRegistered(recipientId, device.Id, device.Platform, !isNew);
            return Result.Success<Outcome>(new Outcome.Registered(new Response(
                device.Id, device.Platform, device.RegisteredAt, device.LastSeenAt)));
        }
    }
}
