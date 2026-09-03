using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

internal enum AttachmentDependencyOutcome
{
    /// <summary>The durable record now says what the caller declared.</summary>
    Recorded,

    /// <summary>
    /// The same dependent already held a live dependency, so nothing was
    /// written: the record keeps the reason and the instant of the hold that
    /// is still running, and the declaration this call carried was dropped.
    /// The object stays protected either way.
    /// </summary>
    AlreadyHeld,

    /// <summary>The reason or the dependent is not usable as an identifier.</summary>
    Invalid,

    /// <summary>No attachment carries the reference.</summary>
    UnknownAttachment,
}

/// <summary>
/// Durable record of who depends on an attachment. Taking a dependency and
/// ending one are both safe to repeat, because the dependents that write here
/// retry, and a retry that piled a second hold would leave the object held by
/// something nobody knows how to release.
/// <para>
/// The two sides answer a repeat differently, and the difference is not
/// cosmetic. Ending a dependency declares nothing beyond the ending, so
/// repeating it drops nothing. Taking one declares a reason and an instant,
/// and a live row keeps the pair it was taken with, so a caller whose
/// declaration was not written is told so instead of being left to believe it
/// landed.
/// </para>
/// </summary>
internal sealed class AttachmentDependencyRegistry(
    AttachmentManagementDbContext dbContext,
    TimeProvider timeProvider)
{
    internal async Task<AttachmentDependencyOutcome> HoldAsync(
        AttachmentReference reference,
        string reason,
        string holder,
        CancellationToken cancellationToken)
    {
        // The dependent's identity is the whole of what a release comes back
        // with, and both values go straight into the row that protects the
        // bytes. Blank text, or text longer than the column, is refused before
        // the write: past this point the row exists, and a hold nobody can
        // name is a hold nobody can end.
        if (!IsUsable(reason, AttachmentDependency.MaxReasonLength)
            || !IsUsable(holder, AttachmentDependency.MaxHolderLength))
        {
            return AttachmentDependencyOutcome.Invalid;
        }

        // The row is taken first so a disposal that already read the live
        // holds cannot be removing the bytes while this hold is written.
        await using IDbContextTransaction transaction =
            await dbContext.Database.BeginTransactionAsync(cancellationToken);
        if (await AttachmentRowLock.AcquireAsync(dbContext, reference, cancellationToken)
            is not { } attachmentId)
        {
            return AttachmentDependencyOutcome.UnknownAttachment;
        }

        var identifier = Guid.CreateVersion7();
        DateTimeOffset acquiredAt = timeProvider.GetUtcNow();

        // A live hold is never revised. Its reason and its instant describe
        // the acquisition that is still running, and letting a later call
        // overwrite them would erase when the protection started and why. The
        // write revives a hold that was already released and touches nothing
        // else, so the number of rows it changed is what separates a hold this
        // call created from one it found.
        var written = await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO attachmentmanagement.attachment_dependency
                (id, attachment_id, reason, holder, acquired_at, released_at, version)
            VALUES
                ({identifier}, {attachmentId}, {reason}, {holder}, {acquiredAt}, NULL, 1)
            ON CONFLICT (attachment_id, holder) DO UPDATE
            SET reason = EXCLUDED.reason,
                acquired_at = EXCLUDED.acquired_at,
                released_at = NULL,
                version = attachment_dependency.version + 1
            WHERE attachment_dependency.released_at IS NOT NULL
            """,
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return written == 1
            ? AttachmentDependencyOutcome.Recorded
            : AttachmentDependencyOutcome.AlreadyHeld;
    }

    /// <summary>
    /// Ends one dependency. It takes no row lock: ending a dependency can only
    /// permit a disposal, and a disposal that read the hold before this commit
    /// refuses, which is the safe side of the race.
    /// </summary>
    internal async Task<AttachmentDependencyOutcome> ReleaseAsync(
        AttachmentReference reference,
        string holder,
        CancellationToken cancellationToken)
    {
        if (!IsUsable(holder, AttachmentDependency.MaxHolderLength))
        {
            return AttachmentDependencyOutcome.Invalid;
        }

        Guid attachmentId = await dbContext.Attachments
            .AsNoTracking()
            .Where(attachment => attachment.Reference == reference)
            .Select(attachment => attachment.Id)
            .SingleOrDefaultAsync(cancellationToken);
        if (attachmentId == Guid.Empty)
        {
            return AttachmentDependencyOutcome.UnknownAttachment;
        }

        DateTimeOffset releasedAt = timeProvider.GetUtcNow();
        await dbContext.AttachmentDependencies
            .Where(dependency => dependency.AttachmentId == attachmentId
                && dependency.Holder == holder
                && dependency.ReleasedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(dependency => dependency.ReleasedAt, releasedAt)
                    .SetProperty(dependency => dependency.Version, dependency => dependency.Version + 1),
                cancellationToken);
        return AttachmentDependencyOutcome.Recorded;
    }

    private static bool IsUsable(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength;
}
