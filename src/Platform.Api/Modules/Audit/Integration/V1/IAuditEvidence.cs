namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>
/// Read surface of the trail for an evidence composer. Reads are always by
/// subject and window: the trail is partitioned by occurrence month and its
/// indexes are local per partition, so a read without a window would scan every
/// month. Nothing here is a business read; the caller is composing proof.
/// </summary>
public interface IAuditEvidence
{
    /// <summary>
    /// The chained links of one subject inside the window, in chain order,
    /// rebuilt from the canonical text of each row.
    /// </summary>
    Task<AuditSubjectLinks> ReadLinksAsync(
        AuditSubject subject,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken);

    /// <summary>
    /// The chained links of several subjects inside the window, in one read,
    /// merged in chain order. A composer that answers about a notification also
    /// answers about the recipient and the device registrations it touched, and
    /// one round trip is the difference between a read and a fan-out.
    /// </summary>
    Task<AuditSubjectLinks> ReadLinksAsync(
        IReadOnlyCollection<AuditSubject> subjects,
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken);

    /// <summary>Approvals recorded for one subject identity, oldest first.</summary>
    Task<IReadOnlyList<ApprovalRecord>> ReadApprovalsAsync(
        string subjectType,
        string subjectId,
        int subjectVersion,
        CancellationToken cancellationToken);
}
