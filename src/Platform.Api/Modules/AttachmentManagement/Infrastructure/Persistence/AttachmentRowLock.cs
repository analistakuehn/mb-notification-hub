using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

internal static class AttachmentRowLock
{
    /// <summary>
    /// Takes the attachment row for the rest of the caller's transaction and
    /// answers its identifier, or nothing when no attachment carries the
    /// reference. Holding the row is what keeps a dependency from being taken
    /// in the window between the moment a disposal reads the live holds and
    /// the moment it removes the bytes.
    /// </summary>
    internal static async Task<Guid?> AcquireAsync(
        AttachmentManagementDbContext dbContext,
        AttachmentReference reference,
        CancellationToken cancellationToken)
    {
        var value = reference.Value;
        List<Guid> identifiers = await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT id AS "Value"
                FROM attachmentmanagement.attachment
                WHERE reference = {value}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        return identifiers.Count == 1 ? identifiers[0] : null;
    }
}
