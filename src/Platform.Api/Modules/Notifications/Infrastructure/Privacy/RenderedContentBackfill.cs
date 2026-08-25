using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

/// <summary>How one pass ended, in counts an operator reads before turning the gate off.</summary>
internal sealed record RenderedContentBackfillResult(
    int Scanned,
    int Masked,
    int AlreadyMasked,
    int NeedsReview);

/// <summary>
/// One pass over rendered content sealed before the two-form envelope existed.
/// Such a row has no masked form to promote, so the pass rebuilds it: it
/// renders the published template again with the masked variables the
/// notification already stores, and substitutes the stored envelope only when
/// the hash of that fresh render matches the <c>content_hash_masked</c> the
/// attempt recorded. The double hash is the oracle that makes the substitution
/// safe, and it is what the hash was written for. A row whose recomputed hash
/// does not match is left exactly as it is and leaves in the review list.
/// </summary>
internal sealed class RenderedContentBackfill(
    NotificationsDbContext db,
    IPublishedTemplateRenderer renderer,
    IEnvelopeCipher cipher,
    IOptions<RenderedContentBackfillOptions> options,
    TimeProvider timeProvider,
    ILogger<RenderedContentBackfill> logger)
{
    /// <summary>The template no longer renders what the attempt shipped.</summary>
    internal const string ReviewReasonRenderFailed = "render-failed";

    /// <summary>The fresh render does not reproduce the recorded masked hash.</summary>
    internal const string ReviewReasonHashMismatch = "hash-mismatch";

    /// <summary>Runs one pass and returns its counts.</summary>
    public async Task<RenderedContentBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        RenderedContentBackfillOptions settings = options.Value;
        DateTimeOffset threshold = timeProvider.GetUtcNow() - settings.Grace;
        List<StoredAttempt> candidates = await db.NotificationAttempts
            .AsNoTracking()
            .Where(attempt => attempt.ContentHashFull != attempt.ContentHashMasked)
            .Join(
                db.Notifications.AsNoTracking(),
                attempt => attempt.NotificationId,
                notification => notification.Id,
                (attempt, notification) => new { attempt, notification })

            // An attempt still inside its notification's validity may yet be
            // sent, and it must send the content it was queued with; every
            // other row has no send left to serve.
            .Where(pair => (pair.attempt.Status != NotificationAttemptStatuses.Queued
                    && pair.attempt.Status != NotificationAttemptStatuses.Sending)
                || pair.notification.ExpiresAt <= threshold)
            .Select(pair => new StoredAttempt(
                pair.attempt.Id,
                pair.attempt.NotificationId,
                pair.attempt.Status,
                pair.attempt.ContentHashMasked,
                pair.attempt.RenderedContentEncrypted,
                new StoredNotification(
                    pair.notification.Application,
                    pair.notification.TemplateKey,
                    pair.notification.VariablesMaskedJson)))
            .Take(settings.BatchSize)
            .ToListAsync(cancellationToken);

        var masked = 0;
        var alreadyMasked = 0;
        var needsReview = 0;
        foreach (StoredAttempt candidate in candidates)
        {
            SubstitutionOutcome outcome = await SettleAsync(candidate, cancellationToken);
            switch (outcome)
            {
                case SubstitutionOutcome.Masked:
                    masked++;
                    break;
                case SubstitutionOutcome.AlreadyMasked:
                    alreadyMasked++;
                    break;
                default:
                    needsReview++;
                    break;
            }
        }

        logger.RenderedContentBackfillFinished(candidates.Count, masked, alreadyMasked, needsReview);
        return new RenderedContentBackfillResult(candidates.Count, masked, alreadyMasked, needsReview);
    }

    private async Task<SubstitutionOutcome> SettleAsync(
        StoredAttempt candidate,
        CancellationToken cancellationToken)
    {
        StoredNotification owner = candidate.Notification;
        SealedRenderedContent stored = await RenderedContentEnvelope.ReadAsync(
            cipher, owner.Application, candidate.SealedContent, cancellationToken);
        using var variables = JsonDocument.Parse(owner.VariablesMaskedJson);
        Result<PublishedTemplateRender> render = await renderer.RenderAsync(
            new PublishedRenderRequest
            {
                Application = owner.Application,
                TemplateKey = owner.TemplateKey,
                Channel = stored.Channel,
                Locale = stored.Locale,
                Variables = variables.RootElement.Clone(),
            },
            cancellationToken);
        if (render.IsFailure)
        {
            logger.RenderedContentNeedsReview(
                candidate.AttemptId, candidate.NotificationId, ReviewReasonRenderFailed);
            return SubstitutionOutcome.NeedsReview;
        }

        RenderedForm rebuilt = render.Value!.Full;
        if (!string.Equals(rebuilt.ContentHash, candidate.ContentHashMasked, StringComparison.Ordinal))
        {
            logger.RenderedContentNeedsReview(
                candidate.AttemptId, candidate.NotificationId, ReviewReasonHashMismatch);
            return SubstitutionOutcome.NeedsReview;
        }

        if (!stored.CarriesMaskedForm
            && string.Equals(stored.Subject, rebuilt.Subject, StringComparison.Ordinal)
            && string.Equals(stored.Body, rebuilt.Body, StringComparison.Ordinal)
            && string.Equals(stored.BodyText, rebuilt.BodyText, StringComparison.Ordinal))
        {
            return SubstitutionOutcome.AlreadyMasked;
        }

        var durableContent = await RenderedContentEnvelope.SealMaskedAsync(
            cipher, owner.Application, stored.Channel, stored.Locale, rebuilt, cancellationToken);

        // Guarded by the exact bytes this pass read: whoever rewrote the
        // envelope meanwhile already settled the row, and this pass must not
        // undo that decision.
        var updated = await db.NotificationAttempts
            .Where(attempt => attempt.Id == candidate.AttemptId
                && attempt.RenderedContentEncrypted == candidate.SealedContent)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    attempt => attempt.RenderedContentEncrypted, durableContent),
                cancellationToken);
        if (updated != 1)
        {
            return SubstitutionOutcome.AlreadyMasked;
        }

        logger.RenderedContentSubstituted(candidate.AttemptId, candidate.NotificationId, candidate.Status);
        return SubstitutionOutcome.Masked;
    }

    private enum SubstitutionOutcome
    {
        Masked,
        AlreadyMasked,
        NeedsReview,
    }

    /// <summary>One candidate of a pass, with everything the substitution needs.</summary>
    private sealed record StoredAttempt(
        Guid AttemptId,
        Guid NotificationId,
        string Status,
        string ContentHashMasked,
        byte[] SealedContent,
        StoredNotification Notification);

    /// <summary>What the owning notification contributes to one substitution.</summary>
    private sealed record StoredNotification(
        string Application,
        string TemplateKey,
        string VariablesMaskedJson);
}
