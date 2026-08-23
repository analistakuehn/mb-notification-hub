using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;

/// <summary>
/// The reconstruction reads of this module, straight over its own store and
/// never cached: the snapshot cache only holds active rows, and every question
/// here is about what the ledger recorded, including what was later removed or
/// invalidated. Masking runs inside this class over the decrypted value, so the
/// plaintext is opened and discarded here.
/// </summary>
internal sealed class ContactHistoryReader(
    ContactConsentDbContext db,
    ContactValueProtector protector) : IContactHistory
{
    public async Task<Result<IReadOnlyList<HistoricalContactPoint>>> DescribeContactPointsAsync(
        string recipientId,
        IReadOnlyCollection<Guid> contactPointIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentNullException.ThrowIfNull(contactPointIds);
        if (contactPointIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<HistoricalContactPoint>>([]);
        }

        Guid[] wanted = [.. contactPointIds.Distinct()];
        List<ContactPoint> points = await db.ContactPoints
            .AsNoTracking()
            .Where(point => point.RecipientId == recipientId && wanted.Contains(point.Id))
            .OrderBy(point => point.Id)
            .ToListAsync(cancellationToken);

        var described = new List<HistoricalContactPoint>(points.Count);
        foreach (ContactPoint point in points)
        {
            var value = await protector.RevealAsync(point.ValueEncrypted, cancellationToken);
            described.Add(new HistoricalContactPoint
            {
                ContactPointId = point.Id,
                Channel = point.Channel,
                MaskedValue = ContactValueMask.Apply(point.Channel, value),
                Verified = point.Verified,
                Active = point.IsActive,
                RemovedAt = point.RemovedAt,
            });
        }

        return Result.Success<IReadOnlyList<HistoricalContactPoint>>(described);
    }

    public async Task<Result<IReadOnlyList<HistoricalDeviceRegistration>>> DescribeDeviceRegistrationsAsync(
        string recipientId,
        IReadOnlyCollection<Guid> deviceTokenIds,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);
        ArgumentNullException.ThrowIfNull(deviceTokenIds);
        if (deviceTokenIds.Count == 0)
        {
            return Result.Success<IReadOnlyList<HistoricalDeviceRegistration>>([]);
        }

        Guid[] wanted = [.. deviceTokenIds.Distinct()];

        // The projection never selects the token column: no later edit of this
        // read can reach a value the contract forbids.
        List<HistoricalDeviceRegistration> described = await db.DeviceTokens
            .AsNoTracking()
            .Where(device => device.RecipientId == recipientId && wanted.Contains(device.Id))
            .OrderBy(device => device.Id)
            .Select(device => new HistoricalDeviceRegistration
            {
                DeviceTokenId = device.Id,
                Platform = device.Platform,
                AppVersion = device.AppVersion,
                RegisteredAt = device.RegisteredAt,
                LastSeenAt = device.LastSeenAt,
                Active = device.InvalidatedAt == null,
                InvalidatedAt = device.InvalidatedAt,
            })
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<HistoricalDeviceRegistration>>(described);
    }

    public async Task<Result<IReadOnlyList<ConsentLedgerEntry>>> ReadConsentLedgerAsync(
        string recipientId,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientId);

        List<ConsentLedgerEntry> entries = await db.Consents
            .AsNoTracking()
            .Where(consent => consent.RecordedAt >= fromInclusive && consent.RecordedAt < toExclusive)
            .Join(
                db.ContactPoints.AsNoTracking().Where(point => point.RecipientId == recipientId),
                consent => consent.ContactPointId,
                point => point.Id,
                (consent, point) => new ConsentLedgerEntry
                {
                    ContactPointId = consent.ContactPointId,
                    Channel = point.Channel,
                    Purpose = consent.Purpose,
                    Granted = consent.Granted,
                    Source = consent.Source,
                    ActorId = consent.ActorId,
                    TermsVersion = consent.TermsVersion,
                    RecordedAt = consent.RecordedAt,
                })
            .OrderBy(entry => entry.RecordedAt)
            .ThenBy(entry => entry.ContactPointId)
            .ToListAsync(cancellationToken);

        return Result.Success<IReadOnlyList<ConsentLedgerEntry>>(entries);
    }
}
