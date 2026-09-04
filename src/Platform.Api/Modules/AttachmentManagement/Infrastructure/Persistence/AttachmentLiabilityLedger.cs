using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// The two statements that annotate an attachment with a repair it is owed and
/// take the annotation back.
/// <para>
/// Set-based on purpose, and only for the value the aggregate cannot own. The
/// writer that records custody is the upload path, which reaches here exactly
/// when its own transaction failed: there is no aggregate left to save, the
/// tracked instance is the one whose save just died, and loading a second copy
/// to save it would put a concurrency token in the way of an annotation that
/// has nothing to do with the state it collides on.
/// </para>
/// <para>
/// Both statements carry the value they expect in the predicate. Recording
/// writes only over an empty column, so a repair already recorded is never
/// replaced by one discovered later; clearing writes only over the exact word
/// the round set out to repair, so a round that took a while cannot erase a
/// different repair that arrived while it worked.
/// </para>
/// </summary>
internal static class AttachmentLiabilityLedger
{
    /// <summary>
    /// Records a repair against the attachment, and answers whether this call
    /// is the one that recorded it.
    /// </summary>
    internal static async Task<bool> RecordAsync(
        AttachmentManagementDbContext dbContext,
        AttachmentReference reference,
        string liability,
        CancellationToken cancellationToken)
        => await dbContext.Attachments
            .Where(attachment => attachment.Reference == reference
                && attachment.ReconciliationLiability == null)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    attachment => attachment.ReconciliationLiability,
                    liability),
                cancellationToken) == 1;

    /// <summary>
    /// Takes back a repair that has been carried out, and answers whether the
    /// row still carried it.
    /// </summary>
    internal static async Task<bool> ClearAsync(
        AttachmentManagementDbContext dbContext,
        Guid attachmentId,
        string liability,
        CancellationToken cancellationToken)
        => await dbContext.Attachments
            .Where(attachment => attachment.Id == attachmentId
                && attachment.ReconciliationLiability == liability)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    attachment => attachment.ReconciliationLiability,
                    (string?)null),
                cancellationToken) == 1;
}
