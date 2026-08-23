using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>
/// One sweep round over abandoned rendered content: attempts that never
/// reached a terminal verdict and whose notification expired past the grace
/// window. The dispatcher settles every attempt it does reach a verdict on, so
/// what lands here is the remainder, a crash between claim and verdict or a
/// message that outlived the notification. Only rows whose two hashes differ
/// are candidates, which is exactly the set that has a complete form to
/// discard.
/// </summary>
internal sealed class RenderedContentSweep(
    NotificationsDbContext db,
    IEnvelopeCipher cipher,
    IOptions<RenderedContentRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RenderedContentSweep> logger)
{
    /// <summary>Runs one round and returns how many attempts were settled.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        RenderedContentRetentionOptions settings = options.Value;
        DateTimeOffset threshold = timeProvider.GetUtcNow() - settings.Grace;
        List<AbandonedAttempt> candidates = await db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.ContentHashFull != attempt.ContentHashMasked
                && (attempt.Status == NotificationAttemptStatuses.Queued
                    || attempt.Status == NotificationAttemptStatuses.Sending))
            .Join(
                db.Notifications.AsNoTracking().Where(notification => notification.ExpiresAt <= threshold),
                attempt => attempt.NotificationId,
                notification => notification.Id,
                (attempt, notification) => new AbandonedAttempt(
                    attempt.Id,
                    attempt.NotificationId,
                    attempt.Status,
                    notification.Application,
                    attempt.RenderedContentEncrypted))
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        var settled = 0;
        foreach (AbandonedAttempt candidate in candidates)
        {
            var durableContent = await RenderedContentEnvelope.TryDiscardCompleteFormAsync(
                cipher, candidate.Application, candidate.SealedContent, cancellationToken);
            if (durableContent is null)
            {
                // Sealed before the transition existed: only a fresh render
                // against the stored masked variables can settle it, which is
                // what the backfill does.
                continue;
            }

            // Guarded by the status this round observed: a dispatcher that
            // claimed or settled the attempt meanwhile wins, and its own
            // verdict already discarded the complete form.
            var updated = await db.NotificationAttempts
                .Where(attempt => attempt.Id == candidate.AttemptId
                    && attempt.Status == candidate.Status)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(
                        attempt => attempt.RenderedContentEncrypted, durableContent),
                    cancellationToken);
            if (updated == 1)
            {
                settled++;
                logger.AbandonedContentMasked(candidate.AttemptId, candidate.NotificationId, candidate.Status);
            }
        }

        if (settled > 0)
        {
            logger.RenderedContentSweepSettled(settled, threshold);
        }

        return settled;
    }

    /// <summary>One candidate of a round, with everything the settlement needs.</summary>
    private sealed record AbandonedAttempt(
        Guid AttemptId,
        Guid NotificationId,
        string Status,
        string Application,
        byte[] SealedContent);
}
