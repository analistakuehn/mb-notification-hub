using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;

/// <summary>
/// The read side of the published contract, straight over this module's own
/// store. The snapshot never materializes a contact value; the reveal read
/// decrypts inside this class and hands out only the plaintext string, so the
/// envelope and the key scope never cross the module boundary.
/// </summary>
internal sealed class RecipientDirectory(
    ContactConsentDbContext db,
    ContactValueProtector protector) : IRecipientDirectory
{
    /// <summary>
    /// The store read has no stale concept: it is the source of truth, so the
    /// fallback preference changes nothing here. The cached decorator is the
    /// layer that honors it.
    /// </summary>
    public Task<Result<RecipientSnapshot>> FindAsync(
        string recipientId,
        RecipientReadFallback fallback,
        CancellationToken cancellationToken)
        => FindAsync(recipientId, cancellationToken);

    public async Task<Result<RecipientSnapshot>> FindAsync(
        string recipientId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);

        RecipientProfile? profile = await db.RecipientProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.RecipientId == recipientId, cancellationToken);
        if (profile is null)
        {
            return Result.NotFound<RecipientSnapshot>(
                $"O destinatário '{recipientId}' não possui cadastro de contatos.");
        }

        List<ContactPointSnapshot> contactPoints = await db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId && point.RemovedAt == null)
            .OrderBy(point => point.Id)
            .Select(point => new ContactPointSnapshot(point.Id, point.Channel, point.Verified))
            .ToListAsync(cancellationToken);

        var consentRecords = await db.Consents
            .AsNoTracking()
            .Join(
                db.ContactPoints.AsNoTracking().Where(point => point.RecipientId == recipientId),
                consent => consent.ContactPointId,
                point => point.Id,
                (consent, point) => new { consent, point.Channel })
            .ToListAsync(cancellationToken);

        // The pair is keyed on the canonical purpose, and the decision carries
        // that key rather than the spelling of the record that won. Records
        // written before the aggregate canonicalized resolve into the same
        // lineage here, which is how the ledger repairs them: the table
        // rejects UPDATE, and the raw declaration stays readable through the
        // ledger read that exists to show what was declared.
        var consents = consentRecords
            .GroupBy(record => (
                Purpose: ConsentPurpose.Canonicalize(record.consent.Purpose),
                record.Channel))
            .Select(group => new
            {
                group.Key.Purpose,
                group.Key.Channel,
                Latest = group
                    .OrderByDescending(record => record.consent.RecordedAt)
                    .ThenByDescending(record => record.consent.Id)
                    .First()
                    .consent,
            })
            .OrderBy(entry => entry.Purpose, StringComparer.Ordinal)
            .ThenBy(entry => entry.Channel, StringComparer.Ordinal)
            .Select(entry => new ConsentDecision(
                entry.Purpose,
                entry.Channel,
                entry.Latest.Granted,
                entry.Latest.Source,
                entry.Latest.TermsVersion,
                entry.Latest.RecordedAt))
            .ToList();

        List<DeviceRegistration> devices = await db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.RecipientId == recipientId && device.InvalidatedAt == null)
            .OrderByDescending(device => device.LastSeenAt)
            .Select(device => new DeviceRegistration(
                device.Id, device.Platform, device.AppVersion, device.LastSeenAt))
            .ToListAsync(cancellationToken);

        // Only the suppressions of contact points this snapshot lists: a
        // suppression of a point already removed answers a question nobody
        // asks here, and leaving it in would put a channel in the suppression
        // list that is not in the contact list.
        List<SuppressionState> suppressions = await db.Suppressions
            .AsNoTracking()
            .Where(suppression => suppression.RemovedAt == null
                && db.ContactPoints.Any(point => point.Id == suppression.ContactPointId
                    && point.RecipientId == recipientId
                    && point.RemovedAt == null))
            .OrderBy(suppression => suppression.ContactPointId)
            .Select(suppression => new SuppressionState(
                suppression.ContactPointId,
                suppression.Channel,
                suppression.Reason,
                suppression.CreatedAt,
                suppression.Until))
            .ToListAsync(cancellationToken);

        return Result.Success(new RecipientSnapshot
        {
            RecipientId = profile.RecipientId,
            Timezone = profile.EffectiveTimezone,
            Locale = profile.Locale,
            ContactPoints = contactPoints,
            Consents = consents,
            Devices = devices,
            Suppressions = suppressions,
        });
    }

    public async Task<Result<string>> RevealContactValueAsync(
        string recipientId,
        Guid contactPointId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);

        ContactPoint? point = await db.ContactPoints
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == contactPointId
                    && candidate.RecipientId == recipientId
                    && candidate.RemovedAt == null,
                cancellationToken);
        if (point is null)
        {
            return Result.NotFound<string>(
                "O ponto de contato não existe, foi removido ou pertence a outro destinatário.");
        }

        var value = await protector.RevealAsync(point.ValueEncrypted, cancellationToken);
        return Result.Success(value);
    }

    public async Task<Result<IReadOnlyList<MaskedContactPoint>>> MaskContactPointsAsync(
        string recipientId,
        IReadOnlyCollection<Guid> contactPointIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentNullException.ThrowIfNull(contactPointIds);
        if (contactPointIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<MaskedContactPoint>>([]);
        }

        Guid[] wanted = [.. contactPointIds.Distinct()];

        // Removed points are included on purpose: the caller asks where a
        // message went, and the removal came after the send.
        List<ContactPoint> points = await db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId && wanted.Contains(point.Id))
            .OrderBy(point => point.Id)
            .ToListAsync(cancellationToken);

        var masked = new List<MaskedContactPoint>(points.Count);
        foreach (ContactPoint point in points)
        {
            var value = await protector.RevealAsync(point.ValueEncrypted, cancellationToken);
            masked.Add(new MaskedContactPoint(
                point.Id,
                point.Channel,
                ContactValueMask.Apply(point.Channel, value),
                point.IsActive));
        }

        return Result.Success<IReadOnlyList<MaskedContactPoint>>(masked);
    }

    public async Task<Result<string>> RevealDeviceTokenAsync(
        string recipientId,
        Guid deviceTokenId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);

        DeviceToken? device = await db.DeviceTokens
            .AsNoTracking()
            .FirstOrDefaultAsync(
                candidate => candidate.Id == deviceTokenId
                    && candidate.RecipientId == recipientId
                    && candidate.InvalidatedAt == null,
                cancellationToken);
        if (device is null)
        {
            return Result.NotFound<string>(
                "O registro de dispositivo não existe, foi invalidado ou pertence a outro destinatário.");
        }

        return Result.Success(device.Token);
    }
}
