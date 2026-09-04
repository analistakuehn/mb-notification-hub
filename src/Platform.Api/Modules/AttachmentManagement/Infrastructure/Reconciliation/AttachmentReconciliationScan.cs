using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;

/// <summary>What one round took on and what it settled.</summary>
internal readonly record struct AttachmentReconciliationResult(
    int Examined,
    int CustodyReclaimed,
    int GenerationsRemoved,
    int VerdictsClosed,
    int Unresolved);

/// <summary>One outstanding repair, with everything the repair is made of.</summary>
internal sealed record AttachmentLiabilityCandidate(
    Guid Id,
    AttachmentReference Reference,
    Guid ContentId,
    string Liability);

/// <summary>
/// Carries out the repairs that attachments are recorded as owing.
/// <para>
/// It exists because a handful of failures land outside the transaction that
/// would have recorded them. A write the store took and did not name, a
/// conflict over a key held by bytes nobody accounted for, a removal the store
/// never confirmed: each of them ends with the request answered and with
/// durable state that no later request can put right, because every retry of
/// that upload meets the same occupied key. A verdict that never concluded
/// ends the same way for a different reason: it waits on a deadline that
/// nothing reaches unless somebody happens to ask for a validation again.
/// </para>
/// <para>
/// Nothing on the accepting path waits for this. A claim is atomic with the
/// acceptance it belongs to and concludes with this round stopped and with no
/// round ever having run; what a round repairs is the residue of failures, and
/// an acceptance that depended on it would be an acceptance that depended on a
/// batch job.
/// </para>
/// <para>
/// The round never invents a repair. It reads the word written on the row and
/// runs the repair that word names, and a word it does not recognise is left
/// exactly where it is: the vocabulary is closed on the writing side, and a
/// round that guessed at an unknown value would be free to clear a repair it
/// never carried out.
/// </para>
/// </summary>
internal sealed class AttachmentReconciliationScan(
    AttachmentManagementDbContext dbContext,
    IAttachmentObjectInventory inventory,
    IAttachmentObjectStore objectStore,
    AttachmentValidation validation,
    IOptions<AttachmentReconciliationOptions> options,
    TimeProvider timeProvider,
    ILogger<AttachmentReconciliationScan> logger)
{
    public async Task<AttachmentReconciliationResult> RunAsync(CancellationToken cancellationToken)
    {
        AttachmentReconciliationOptions settings = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        List<AttachmentLiabilityCandidate> candidates =
            await OutstandingQuery(dbContext, now, settings.BatchSize)
                .ToListAsync(cancellationToken);

        var reclaimed = 0;
        var removed = 0;
        var closed = 0;
        var unresolved = 0;
        foreach (AttachmentLiabilityCandidate candidate in candidates)
        {
            switch (candidate.Liability)
            {
                case AttachmentLiabilities.CustodyUnreclaimed:
                    AttachmentCustodyRepair repair = await ReclaimAsync(
                        candidate, cancellationToken);
                    removed += repair.Removed;
                    if (repair.Settled) reclaimed++;
                    else unresolved++;

                    break;

                case AttachmentLiabilities.VerdictOpen:
                    if (await CloseVerdictAsync(candidate, cancellationToken)) closed++;
                    else unresolved++;

                    break;

                default:
                    unresolved++;
                    logger.AttachmentLiabilityNotUnderstood(candidate.Reference.Value);
                    break;
            }
        }

        if (candidates.Count > 0)
        {
            logger.AttachmentReconciliationRoundCompleted(
                candidates.Count, reclaimed, removed, closed, unresolved);
        }

        return new AttachmentReconciliationResult(
            candidates.Count, reclaimed, removed, closed, unresolved);
    }

    /// <summary>
    /// The selection itself, composed and not executed, so a plan assertion
    /// reads the statement this code sends instead of a transcription of it.
    /// <para>
    /// The emptiness test on the column is written first and on its own,
    /// because it is what the partial index is built on: the planner reads
    /// that index only for a statement whose conditions imply the index
    /// predicate, and the whole value of this job is in reading a structure
    /// the size of the backlog. Nothing about that failing is visible from
    /// outside; the round would keep returning the same rows while quietly
    /// becoming a walk of every attachment ever registered.
    /// </para>
    /// <para>
    /// The deadline of a waiting verdict is part of the selection and not of
    /// the loop, so the batch is filled with work instead of with rows that
    /// are not due yet. A round that read them anyway would spend its whole
    /// batch on attachments it is going to skip, and the repairs behind them
    /// would never be reached.
    /// </para>
    /// <para>
    /// Oldest first, by the key of that same index, so the order costs no sort
    /// and a backlog larger than one batch drains from the end that has been
    /// waiting longest.
    /// </para>
    /// </summary>
    internal static IQueryable<AttachmentLiabilityCandidate> OutstandingQuery(
        AttachmentManagementDbContext dbContext,
        DateTimeOffset now,
        int batchSize)
        => dbContext.Attachments
            .AsNoTracking()
            .Where(attachment => attachment.ReconciliationLiability != null
                && (attachment.ReconciliationLiability != AttachmentLiabilities.VerdictOpen
                    || (attachment.InconclusiveUntil != null
                        && attachment.InconclusiveUntil <= now)))
            .OrderBy(attachment => attachment.CreatedAt)
            .Take(batchSize)
            .Select(attachment => new AttachmentLiabilityCandidate(
                attachment.Id,
                attachment.Reference,
                attachment.ContentId,
                attachment.ReconciliationLiability!));

    /// <summary>
    /// Gives the key back by removing everything under it that the record does
    /// not claim.
    /// <para>
    /// The key is derived from the row and never stored, which is what makes
    /// this reachable at all: the generation of an orphan was never learned,
    /// so there is nothing to look it up by, and the only way to name one is
    /// to ask the store what the derived key holds and to subtract what the
    /// record accounts for.
    /// </para>
    /// <para>
    /// An inventory the store could not complete stops the repair before
    /// anything is removed. Subtracting a recorded generation from a listing
    /// that is missing entries would remove nothing wrong, but concluding
    /// from it that the key is clean would clear a repair that was never
    /// carried out, and the row would leave the backlog with the bytes still
    /// there.
    /// </para>
    /// <para>
    /// A removal the store did not confirm stops it as well, and the repair
    /// stays on the row for the next round. Counting an unconfirmed removal as
    /// done is the one mistake this job cannot recover from by running again.
    /// </para>
    /// </summary>
    private async Task<AttachmentCustodyRepair> ReclaimAsync(
        AttachmentLiabilityCandidate candidate,
        CancellationToken cancellationToken)
    {
        AttachmentKeyInventory holdings = await inventory.ListAsync(
            candidate.ContentId, cancellationToken);
        if (holdings.Status != AttachmentKeyInventoryStatus.Listed)
        {
            logger.AttachmentInventoryUnavailable(candidate.Reference.Value);
            return new AttachmentCustodyRepair(Settled: false, Removed: 0);
        }

        List<string> recorded = await dbContext.ObjectGenerations
            .AsNoTracking()
            .Where(generation => generation.AttachmentId == candidate.Id)
            .Select(generation => generation.Version)
            .ToListAsync(cancellationToken);
        var claimed = new HashSet<string>(recorded, StringComparer.Ordinal);

        var removed = 0;
        foreach (AttachmentObjectLocator generation in holdings.Generations)
        {
            if (claimed.Contains(generation.Version)) continue;

            if (await objectStore.DiscardAsync(generation, cancellationToken)
                != AttachmentObjectDiscard.Removed)
            {
                logger.AttachmentOrphanNotRemoved(candidate.Reference.Value, removed);
                return new AttachmentCustodyRepair(Settled: false, removed);
            }

            removed++;
        }

        if (!await AttachmentLiabilityLedger.ClearAsync(
            dbContext,
            candidate.Id,
            AttachmentLiabilities.CustodyUnreclaimed,
            cancellationToken))
        {
            return new AttachmentCustodyRepair(Settled: false, removed);
        }

        logger.AttachmentCustodyReclaimed(candidate.Reference.Value, removed);
        return new AttachmentCustodyRepair(Settled: true, removed);
    }

    /// <summary>
    /// Ends a wait whose deadline has passed, through the operation that owns
    /// the state machine.
    /// <para>
    /// It calls the validation rather than writing the transition itself, and
    /// that is the whole design. The deadline is read there, before a verdict
    /// is asked for, and the refusal it writes is the same refusal a producer
    /// would have got by asking again; a round with a transition of its own
    /// would be a second state machine over the same attachments, free to
    /// conclude what that one would never conclude. The repair is taken off
    /// the row by that transition, in the transaction that ends the wait, and
    /// never by this method.
    /// </para>
    /// <para>
    /// The selection already left out the rows whose deadline has not passed,
    /// so this never asks the policy about an attachment that is still inside
    /// its tolerance.
    /// </para>
    /// </summary>
    private async Task<bool> CloseVerdictAsync(
        AttachmentLiabilityCandidate candidate,
        CancellationToken cancellationToken)
    {
        AttachmentValidationOutcome outcome = await validation.ValidateAsync(
            candidate.Reference, cancellationToken);
        if (outcome.Status == AttachmentValidationStatus.Rejected)
        {
            logger.AttachmentWaitClosed(candidate.Reference.Value);
            return true;
        }

        logger.AttachmentWaitNotClosed(candidate.Reference.Value, outcome.Status.ToString());
        return false;
    }

    private readonly record struct AttachmentCustodyRepair(bool Settled, int Removed);
}
