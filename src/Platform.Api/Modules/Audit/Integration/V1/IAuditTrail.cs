using System.Data.Common;

namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>
/// Transactional write surface of the audit trail. Every method executes its
/// insert on the caller's open database transaction, so the governed effect
/// and its trail commit together or not at all; an asynchronous trail is a
/// defect by design. The caller keeps its own persistence isolated: it hands
/// over the raw transaction, never a context or an entity.
/// </summary>
public interface IAuditTrail
{
    /// <summary>
    /// Appends one audit event inside <paramref name="transaction"/>, linking
    /// it to the hash chain of the monthly partition of its occurrence. The
    /// chain lock is held until the caller's transaction completes, so the
    /// caller must commit promptly after this call.
    /// </summary>
    Task AppendAsync(DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken);

    /// <summary>Records one approval inside <paramref name="transaction"/>.</summary>
    Task RecordApprovalAsync(DbTransaction transaction, ApprovalGrant grant, CancellationToken cancellationToken);
}
