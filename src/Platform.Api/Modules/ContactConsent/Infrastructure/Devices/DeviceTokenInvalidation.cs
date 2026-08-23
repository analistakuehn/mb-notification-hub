using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Devices;

/// <summary>
/// Write side of the published token lifecycle: stamps the invalidation, the
/// cache-invalidation outbox message and the audit event in one transaction,
/// through the same transactional writer every other write of this module
/// uses. A repeated report of an already invalidated token is a declarative
/// no-op: no state change, no second cache event, its own short audit trail.
/// </summary>
internal sealed class DeviceTokenInvalidation(
    ContactConsentDbContext db,
    ContactConsentWriter writer,
    TimeProvider timeProvider,
    ILogger<DeviceTokenInvalidation> logger) : IDeviceTokenLifecycle
{
    /// <summary>Actor identity of the provider feedback path; the report always arrives from the dispatch side.</summary>
    internal const string ActorIdProviderFeedback = "dispatcher";

    public async Task<Result> InvalidateDeviceTokenAsync(
        string recipientId,
        Guid deviceTokenId,
        string reason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        DateTimeOffset now = timeProvider.GetUtcNow();
        DeviceToken? device = await db.DeviceTokens
            .FirstOrDefaultAsync(
                candidate => candidate.Id == deviceTokenId && candidate.RecipientId == recipientId,
                cancellationToken);
        if (device is null)
        {
            return new Result(false, ResultErrorKind.NotFound,
                "O registro de dispositivo não existe ou pertence a outro destinatário.");
        }

        var alreadyInvalidated = !device.IsActive;

        // The provider feedback arrives as a call from the dispatch side, not
        // as a record: nothing here is deduplicated by mark, and the repeated
        // report is already idempotent by state.
        var writeContext = new ContactWriteContext(
            ActorIdProviderFeedback, ContactConsentAuditVocabulary.ActorTypeSystem, Provenance: null);
        AuditEntry auditEntry = new()
        {
            ActorType = ContactConsentAuditVocabulary.ActorTypeSystem,
            ActorId = ActorIdProviderFeedback,
            Application = null,
            Action = ContactConsentAuditVocabulary.DeviceInvalidated,
            EntityType = ContactConsentAuditVocabulary.EntityTypeDeviceToken,
            EntityId = device.Id.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                recipientId,
                platform = device.Platform,
                reason,
                alreadyInvalidated,
            }),
            OccurredAt = now,
        };

        if (alreadyInvalidated)
        {
            // Declarative no-op: the active token set did not change, so no
            // cache event; only the trail of the repeated report.
            await writer.AppendStandaloneAuditAsync(writeContext, auditEntry, cancellationToken);
            logger.DeviceInvalidationRepeated(recipientId, device.Id, reason);
            return Result.Success();
        }

        device.Invalidate(now);
        ContactWriteOutcome outcome = await writer.CommitAsync(
            writeContext,
            [ContactConsentEvents.Build(ContactConsentEvents.ContactChanged, recipientId, null, now)],
            auditEntry,
            cancellationToken);
        if (outcome is ContactWriteOutcome.ConcurrencyConflict)
        {
            return Result.BusinessRuleViolation(
                "Uma escrita concorrente venceu a corrida; repita a invalidação.");
        }

        logger.DeviceInvalidated(recipientId, device.Id, reason);
        return Result.Success();
    }
}
