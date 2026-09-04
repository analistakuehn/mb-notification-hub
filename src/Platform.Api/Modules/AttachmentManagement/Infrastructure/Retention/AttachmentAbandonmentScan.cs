using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;

/// <summary>What one round examined and what it settled.</summary>
internal readonly record struct AttachmentAbandonmentResult(
    int Examined,
    int Discarded,
    int Preserved,
    int Unresolved,
    int GenerationsRemoved,
    int UnrecordedRemoved);

/// <summary>How one candidate ended.</summary>
internal enum AttachmentAbandonmentOutcome
{
    /// <summary>The content is gone and the record says so.</summary>
    Discarded,

    /// <summary>
    /// Something still depends on the attachment, so nothing was removed. It
    /// is the answer the disposal gives and never one this sweep decides.
    /// </summary>
    Preserved,

    /// <summary>
    /// The attachment was no longer abandoned when the row was taken, so it
    /// was never offered. Nothing was removed and nothing is owed.
    /// </summary>
    NotAbandoned,

    /// <summary>
    /// The round could not finish this one. Whatever it did remove is gone,
    /// the state is unchanged, and the next round meets it again.
    /// </summary>
    Unresolved,
}

/// <summary>One abandoned attachment, with everything the sweep needs of it.</summary>
internal sealed record AttachmentAbandonmentCandidate(
    Guid Id,
    AttachmentReference Reference,
    Guid ContentId,
    string State);

/// <summary>What one candidate cost and how it ended.</summary>
internal readonly record struct AttachmentAbandonmentReport(
    AttachmentAbandonmentOutcome Outcome,
    int GenerationsRemoved,
    int UnrecordedRemoved);

/// <summary>
/// Removes the content of attachments nothing is doing anything with.
/// <para>
/// It decides which attachments are offered and when, and it decides nothing
/// else. Whether an attachment may lose its bytes is the disposal's answer,
/// read from the dependencies in the transaction that holds the row, and this
/// sweep neither repeats that reading nor carries a way to turn it off.
/// </para>
/// <para>
/// Abandonment is a state plus a window, and the window is counted from the
/// last event that could still have changed the state. Counted from the
/// creation it would put a ceiling on the life of an attachment that is being
/// used exactly as intended: an attachment registered a year ago and uploaded
/// this morning is a working attachment, and the rule that ends it has to
/// start at the upload.
/// </para>
/// <para>
/// The whole of the key is swept, and not only the generations the record
/// names. A write whose answer was lost leaves bytes under the derived key
/// that nothing anywhere names, and this is the one place they can be reached:
/// the request that meets the resulting conflict cannot record them, because
/// annotating the row from there breaks a concurrent upload that is about to
/// succeed. Here there is no concurrent upload to break, because the row is
/// held from before the decision until after the removals.
/// </para>
/// <para>
/// Nothing is purged from the record. The line of a generation outlives the
/// bytes it names, and that is the whole of what is left to answer what an
/// attachment held once the content is gone.
/// </para>
/// </summary>
internal sealed class AttachmentAbandonmentScan(
    AttachmentManagementDbContext dbContext,
    AttachmentDisposal disposal,
    IAttachmentObjectInventory inventory,
    IAttachmentObjectStore objectStore,
    IOptions<AttachmentRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<AttachmentAbandonmentScan> logger)
{
    public async Task<AttachmentAbandonmentResult> RunAsync(CancellationToken cancellationToken)
    {
        AttachmentRetentionOptions settings = options.Value;
        AttachmentRetentionWindows windows = settings.Windows();
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<AttachmentAbandonmentCandidate> candidates =
            await AbandonedQuery(dbContext, now, windows, settings.BatchSize)
                .ToListAsync(cancellationToken);

        var discarded = 0;
        var preserved = 0;
        var unresolved = 0;
        var generations = 0;
        var unrecorded = 0;
        foreach (AttachmentAbandonmentCandidate candidate in candidates)
        {
            AttachmentAbandonmentReport report = await DiscardAsync(
                candidate, now, windows, cancellationToken);
            generations += report.GenerationsRemoved;
            unrecorded += report.UnrecordedRemoved;
            switch (report.Outcome)
            {
                case AttachmentAbandonmentOutcome.Discarded:
                    discarded++;
                    break;

                case AttachmentAbandonmentOutcome.Preserved:
                    preserved++;
                    break;

                case AttachmentAbandonmentOutcome.Unresolved:
                    unresolved++;
                    break;

                default:
                    break;
            }
        }

        if (candidates.Count > 0)
        {
            logger.AttachmentAbandonmentRoundCompleted(
                candidates.Count, discarded, generations, unrecorded, preserved, unresolved);
        }

        return new AttachmentAbandonmentResult(
            candidates.Count, discarded, preserved, unresolved, generations, unrecorded);
    }

    /// <summary>
    /// The selection itself, composed and not executed, so a reading of the
    /// rule reads the statement this code sends instead of a transcription of
    /// it.
    /// <para>
    /// Four states and four instants, because each state is abandoned from a
    /// different event: the registration for an upload that never started, the
    /// arrival of the bytes for content nobody asked a verdict about, and the
    /// ending itself for a refusal and for a withdrawal. The three that are
    /// absent are absent by decision and the aggregate says why.
    /// </para>
    /// <para>
    /// Oldest first, by the key of the index the filter is built on, so a
    /// backlog larger than one batch drains from the end that has been waiting
    /// longest. The order is the creation instant and the conditions are not,
    /// which the index cannot help with: one index cannot answer four
    /// comparisons over four columns, so what it gives is the working set and
    /// the order, and the comparisons are rechecked on what it returns.
    /// </para>
    /// </summary>
    internal static IQueryable<AttachmentAbandonmentCandidate> AbandonedQuery(
        AttachmentManagementDbContext dbContext,
        DateTimeOffset now,
        AttachmentRetentionWindows windows,
        int batchSize)
    {
        DateTimeOffset unstarted = now - windows.UnstartedUpload;
        DateTimeOffset unvalidated = now - windows.UnvalidatedContent;
        DateTimeOffset refused = now - windows.RefusedContent;
        DateTimeOffset withdrawn = now - windows.WithdrawnRelease;
        return dbContext.Attachments
            .AsNoTracking()
            .Where(attachment =>
                (attachment.State == AttachmentStates.AwaitingUpload
                    && attachment.CreatedAt <= unstarted)
                || (attachment.State == AttachmentStates.Received
                    && attachment.ReceivedAt != null
                    && attachment.ReceivedAt <= unvalidated)
                || (attachment.State == AttachmentStates.Rejected
                    && attachment.EndedAt != null
                    && attachment.EndedAt <= refused)
                || (attachment.State == AttachmentStates.Revoked
                    && attachment.EndedAt != null
                    && attachment.EndedAt <= withdrawn))
            .OrderBy(attachment => attachment.CreatedAt)
            .Take(batchSize)
            .Select(attachment => new AttachmentAbandonmentCandidate(
                attachment.Id,
                attachment.Reference,
                attachment.ContentId,
                attachment.State));
    }

    /// <summary>
    /// Takes the content of one candidate, under the row and in one
    /// transaction.
    /// <para>
    /// The row is taken before the state is read again, and that order is the
    /// whole guard against the harm this job could do. A candidate is chosen
    /// from a reading that is already in the past, and the producer of an
    /// attachment that was abandoned for days may be uploading to it right
    /// now; a sweep that trusted the selection would remove the bytes of that
    /// upload. Read again under the row, an attachment that stopped being
    /// abandoned is left alone, and one that is still abandoned cannot start
    /// being used until this transaction ends.
    /// </para>
    /// <para>
    /// The state is written last and only after the store has confirmed
    /// everything. A state written earlier would say the content is gone while
    /// bytes were still under the key, and the row would have left the
    /// selection with nothing left to find it by.
    /// </para>
    /// </summary>
    internal async Task<AttachmentAbandonmentReport> DiscardAsync(
        AttachmentAbandonmentCandidate candidate,
        DateTimeOffset now,
        AttachmentRetentionWindows windows,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await AttachmentRowLock.AcquireAsync(dbContext, candidate.Reference, cancellationToken)
            is not { } attachmentId)
        {
            logger.AttachmentNoLongerPresent(candidate.Reference.Value);
            return Report(AttachmentAbandonmentOutcome.Unresolved);
        }

        Attachment attachment = await dbContext.Attachments
            .SingleAsync(row => row.Id == attachmentId, cancellationToken);
        if (attachment.DiscardableFrom(windows) is not { } deadline || now < deadline)
        {
            logger.AttachmentNoLongerAbandoned(candidate.Reference.Value, attachment.State);
            return Report(AttachmentAbandonmentOutcome.NotAbandoned);
        }

        // The refusal belongs here and to nothing else in this file. What it
        // reads is the dependencies, in this transaction and under this row,
        // so a hold taken between the selection and now is already visible and
        // one taken after it cannot be written until this ends.
        AttachmentDisposalOutcome removal = await disposal.DiscardHeldAsync(
            attachmentId, candidate.Reference, cancellationToken);
        if (removal.Status == AttachmentDisposalStatus.HeldByDependency)
        {
            return Report(AttachmentAbandonmentOutcome.Preserved);
        }

        if (removal.Status != AttachmentDisposalStatus.Discarded)
        {
            logger.AttachmentRemovalUnconfirmed(
                candidate.Reference.Value, removal.UnconfirmedRemovals);
            return Report(
                AttachmentAbandonmentOutcome.Unresolved,
                removal.DiscardedGenerations);
        }

        // Everything the record does not account for. It is read after the
        // recorded generations are gone, so what is left under the key is what
        // nothing names, and the removals above are the reason this is a
        // subtraction that costs no comparison.
        AttachmentKeyInventory holdings = await inventory.ListAsync(
            attachment.ContentId, cancellationToken);
        if (holdings.Status != AttachmentKeyInventoryStatus.Listed)
        {
            logger.AttachmentKeyNotListed(candidate.Reference.Value);
            return Report(
                AttachmentAbandonmentOutcome.Unresolved,
                removal.DiscardedGenerations);
        }

        var unrecorded = 0;
        foreach (AttachmentObjectLocator generation in holdings.Generations)
        {
            if (await objectStore.DiscardAsync(generation, cancellationToken)
                != AttachmentObjectDiscard.Removed)
            {
                logger.AttachmentUnrecordedNotRemoved(candidate.Reference.Value, unrecorded);
                return Report(
                    AttachmentAbandonmentOutcome.Unresolved,
                    removal.DiscardedGenerations,
                    unrecorded);
            }

            unrecorded++;
        }

        // Unreachable today: the same rule was asked of the same instance a
        // few lines above, with the same instant and the same windows, and
        // nothing since then reloads it. It has no runtime falsifier here and
        // is not presented as a proven branch. It stays because the day this
        // method rereads the attachment is the day a state that stopped being
        // abandoned could be written as discarded.
        if (attachment.Discard(now, windows) != AttachmentDiscardTransition.Applied)
        {
            logger.AttachmentNoLongerAbandoned(candidate.Reference.Value, attachment.State);
            return Report(
                AttachmentAbandonmentOutcome.Unresolved,
                removal.DiscardedGenerations,
                unrecorded);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.AttachmentDiscarded(
            candidate.Reference.Value,
            candidate.State,
            removal.DiscardedGenerations,
            unrecorded);
        return Report(
            AttachmentAbandonmentOutcome.Discarded,
            removal.DiscardedGenerations,
            unrecorded);
    }

    private static AttachmentAbandonmentReport Report(
        AttachmentAbandonmentOutcome outcome,
        int generations = 0,
        int unrecorded = 0)
        => new(outcome, generations, unrecorded);
}
