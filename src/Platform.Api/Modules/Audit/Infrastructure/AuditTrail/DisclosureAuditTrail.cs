using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;

/// <summary>
/// Records one disclosure in a transaction of its own. A disclosure has no
/// governed effect to share a commit with, so this module opens the shortest
/// possible transaction over its own connection, appends the link, and commits
/// at once. The chain advisory lock is held until that commit: nothing else may
/// run inside the transaction, and the caller must have finished every read
/// before it calls.
/// </summary>
internal sealed class DisclosureAuditTrail(AuditDbContext db, IAuditTrail trail) : IAuditDisclosureTrail
{
    public async Task RecordAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            throw new ArgumentException(
                "Uma divulgação precisa registrar ao menos um sujeito.", nameof(entries));
        }

        await using IDbContextTransaction transaction =
            await db.Database.BeginTransactionAsync(cancellationToken);
        foreach (AuditEntry entry in entries)
        {
            await trail.AppendAsync(transaction.GetDbTransaction(), entry, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
