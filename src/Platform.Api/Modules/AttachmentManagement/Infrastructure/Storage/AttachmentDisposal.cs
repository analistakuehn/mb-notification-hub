using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

internal enum AttachmentDisposalStatus
{
    /// <summary>
    /// The store confirmed the removal of every recorded generation, each one
    /// named by its exact generation.
    /// </summary>
    Discarded,

    /// <summary>Something still depends on the attachment, so nothing was removed.</summary>
    HeldByDependency,

    /// <summary>
    /// The store did not confirm every removal it was asked for, so at least
    /// one recorded generation has to be counted as still stored.
    /// </summary>
    StoreUnavailable,

    /// <summary>No attachment carries the reference.</summary>
    UnknownAttachment,
}

/// <summary>
/// What one disposal did. The two removal counts are the ones the store
/// confirmed and the ones it was asked for and left unconfirmed, never the
/// number of calls made. A disposal that refused asks for no removal, so both
/// are zero there and neither says anything about what is still stored.
/// </summary>
internal sealed record AttachmentDisposalOutcome(
    AttachmentDisposalStatus Status,
    int DiscardedGenerations,
    int UnconfirmedRemovals,
    int LiveDependencies);

/// <summary>
/// Removes the bytes of one attachment, and refuses while anything still
/// depends on it. Which attachments are offered to it, and when, is not
/// decided here.
/// <para>
/// It acts on the generations the module recorded, which is the only
/// inventory it owns. A generation the store kept and the module never
/// learned about is out of its reach by construction, and reconciling those
/// is not this operation's job.
/// </para>
/// </summary>
internal sealed class AttachmentDisposal(
    AttachmentManagementDbContext dbContext,
    IAttachmentObjectStore objectStore,
    ILogger<AttachmentDisposal> logger)
{
    internal async Task<AttachmentDisposalOutcome> DiscardAsync(
        AttachmentReference reference,
        CancellationToken cancellationToken)
    {
        // The row stays taken until the bytes are gone, so a dependency
        // recorded after this read cannot find the object already removed.
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await AttachmentRowLock.AcquireAsync(dbContext, reference, cancellationToken)
            is not { } attachmentId)
        {
            return new AttachmentDisposalOutcome(
                AttachmentDisposalStatus.UnknownAttachment,
                DiscardedGenerations: 0,
                UnconfirmedRemovals: 0,
                LiveDependencies: 0);
        }

        AttachmentDisposalOutcome outcome = await DiscardHeldAsync(
            attachmentId,
            reference,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    /// <summary>
    /// The same disposal, for a caller that already holds the attachment row
    /// and owns the transaction it is held in.
    /// <para>
    /// It exists because deciding that an attachment is abandoned and removing
    /// its bytes have to happen under one lock. A caller that read the state,
    /// let go, and called the method above would be acting on a reading that
    /// an upload could have made false in between, and it would remove the
    /// bytes of the upload that made it false.
    /// </para>
    /// <para>
    /// What the caller does not get is the refusal. The dependency is read
    /// here, in the transaction the caller holds the row in, and no argument
    /// of this method can turn it off: a second reading of that rule anywhere
    /// else would be a second place for it to be wrong.
    /// </para>
    /// </summary>
    internal async Task<AttachmentDisposalOutcome> DiscardHeldAsync(
        Guid attachmentId,
        AttachmentReference reference,
        CancellationToken cancellationToken)
    {
        // A dependency is live while nothing released it. The reason it names
        // is not consulted, and neither is how long it has been there: an
        // outcome nobody reported is exactly the case this protection exists
        // for.
        var live = await dbContext.AttachmentDependencies
            .AsNoTracking()
            .CountAsync(
                dependency => dependency.AttachmentId == attachmentId
                    && dependency.ReleasedAt == null,
                cancellationToken);
        if (live > 0)
        {
            logger.AttachmentDisposalHeld(reference.Value, live);
            return new AttachmentDisposalOutcome(
                AttachmentDisposalStatus.HeldByDependency,
                DiscardedGenerations: 0,
                UnconfirmedRemovals: 0,
                LiveDependencies: live);
        }

        List<AttachmentObjectGeneration> generations = await dbContext.ObjectGenerations
            .AsNoTracking()
            .Where(generation => generation.AttachmentId == attachmentId)
            .ToListAsync(cancellationToken);

        // Each removal is counted only when the store said it happened. A
        // removal that failed and one that succeeded look the same from here
        // unless the store is asked, and reporting the pair as discarded would
        // hand a later sweep permission to forget bytes that are still stored.
        var discarded = 0;
        foreach (AttachmentObjectGeneration generation in generations)
        {
            if (await objectStore.DiscardAsync(generation.Locator(), cancellationToken)
                == AttachmentObjectDiscard.Removed)
            {
                discarded++;
            }
        }

        var unconfirmed = generations.Count - discarded;
        if (unconfirmed > 0)
        {
            logger.AttachmentDisposalUnconfirmed(reference.Value, discarded, unconfirmed);
            return new AttachmentDisposalOutcome(
                AttachmentDisposalStatus.StoreUnavailable,
                discarded,
                unconfirmed,
                LiveDependencies: 0);
        }

        logger.AttachmentDisposalCompleted(reference.Value, discarded);
        return new AttachmentDisposalOutcome(
            AttachmentDisposalStatus.Discarded,
            discarded,
            UnconfirmedRemovals: 0,
            LiveDependencies: 0);
    }
}
