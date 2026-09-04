using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Revocation;

internal enum AttachmentRevocationStatus
{
    /// <summary>This call took the release back, and a row records it.</summary>
    Revoked,

    /// <summary>
    /// A previous call already took it back. Nothing was written, no second
    /// row exists, and the instant of the withdrawal is still the first one.
    /// </summary>
    AlreadyRevoked,

    /// <summary>There is no release here to take back. Nothing was written.</summary>
    NotReleased,

    /// <summary>
    /// The state says released and no release row names the grant, so the
    /// module cannot say which approval a withdrawal would be about. Nothing
    /// is written, and nothing is deliverable either: what a later check reads
    /// is the release, and there is none.
    /// </summary>
    ReleaseUnavailable,

    /// <summary>
    /// The reason the caller declared is not something the durable state can
    /// hold. Nothing is written and the release stays in force.
    /// </summary>
    InvalidReason,

    /// <summary>No attachment carries the reference.</summary>
    UnknownAttachment,
}

/// <summary>
/// The act that takes a release back. It is a different operation from the one
/// that decides a verdict, and the difference is the whole of what separates a
/// revocation from a refusal.
/// <para>
/// A refusal is the outcome of reading the content: it belongs to the
/// validation, it is written where the validation writes, and its fine detail
/// names the check that refused. A revocation reads nothing. It acts on a
/// grant that already exists, it names that exact grant, and it says nothing
/// about the bytes, which is why it leaves the durable detail of the
/// validation exactly as it found it.
/// </para>
/// <para>
/// It is not a revalidation and it restarts nothing. The release line is
/// written complete and never revised, so this operation adds a row of its own
/// beside it instead of reaching into one; and the attachment ends here, so
/// nothing that repeats can grant a second release or move the instant of the
/// first one.
/// </para>
/// </summary>
internal sealed class AttachmentRevocationOperation(
    AttachmentManagementDbContext dbContext,
    IAttachmentSaveOperation saveOperation,
    TimeProvider timeProvider,
    ILogger<AttachmentRevocationOperation> logger)
{
    internal async Task<AttachmentRevocationStatus> RevokeAsync(
        AttachmentReference reference,
        string reason,
        CancellationToken cancellationToken)
    {
        // The reason is checked before anything is taken or read. Past the
        // write the row exists, and a withdrawal whose reason was cut down to
        // fit reads back as a sentence nobody wrote.
        if (!AttachmentRevocation.IsUsableReason(reason))
        {
            logger.AttachmentRevocationReasonUnusable(reference.Value, reason?.Length ?? 0);
            return AttachmentRevocationStatus.InvalidReason;
        }

        // The row is held for the rest of the transaction, so a revocation and
        // a validation of the same attachment cannot both read a state that is
        // about to stop being true.
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await AttachmentRowLock.AcquireAsync(dbContext, reference, cancellationToken)
            is not { } attachmentId)
        {
            return AttachmentRevocationStatus.UnknownAttachment;
        }

        Attachment attachment = await dbContext.Attachments
            .SingleAsync(candidate => candidate.Id == attachmentId, cancellationToken);

        // Asked before the release is looked up at all, so a repeat costs no
        // search and, more to the point, writes nothing: the answer to the
        // second call is the state the first one left behind.
        if (attachment.RevocationRefusal() is { } refusal)
        {
            if (refusal == AttachmentRevocationTransition.AlreadyRevoked)
            {
                logger.AttachmentAlreadyRevoked(reference.Value);
                return AttachmentRevocationStatus.AlreadyRevoked;
            }

            logger.AttachmentNotReleased(reference.Value, attachment.State);
            return AttachmentRevocationStatus.NotReleased;
        }

        // The grant in force is the most recent one. Naming the first would
        // take back an approval that a later one had already superseded, and
        // that is the shape the release line takes the day an explicit
        // revalidation writes a second row into it.
        Guid? releaseId = await dbContext.Releases
            .AsNoTracking()
            .Where(release => release.AttachmentId == attachmentId)
            .OrderByDescending(release => release.ReleasedAt)
            .ThenByDescending(release => release.Id)
            .Select(release => (Guid?)release.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (releaseId is not { } grant)
        {
            logger.AttachmentReleaseUnavailable(reference.Value);
            return AttachmentRevocationStatus.ReleaseUnavailable;
        }

        // The state was read under the row lock a few lines above and nothing
        // since then reloads it, so this transition always applies.
        //
        // The instant is read once and written twice, on the attachment and on
        // the row. Two readings would let the record of the withdrawal and the
        // clock its retention is counted from disagree about when it happened.
        DateTimeOffset revokedAt = timeProvider.GetUtcNow();
        _ = attachment.Revoke(revokedAt);
        dbContext.Revocations.Add(AttachmentRevocation.Record(
            attachmentId,
            grant,
            reason,
            revokedAt));

        // The state and the row become durable together, for the reason the
        // release was written that way: a revoked state without the row would
        // be a withdrawal nothing can date, and a row without the state would
        // be a withdrawal nothing points at.
        await saveOperation.SaveChangesAsync(dbContext, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.AttachmentRevoked(reference.Value, reason);
        return AttachmentRevocationStatus.Revoked;
    }
}
