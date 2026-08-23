namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>
/// Write surface for a disclosure: the trail row that records evidence leaving
/// the hub. Unlike <see cref="IAuditTrail"/>, this one owns its transaction,
/// because a disclosure has no governed effect to share a commit with. The
/// caller composes the evidence first, records the disclosure second, and only
/// then writes the first byte of the answer: a failure here must abort the
/// response, so nothing is disclosed without its record.
/// </summary>
/// <remarks>
/// Deliberately not an outbox: an outbox would place the record after the
/// egress and open exactly the window an insider needs. The transaction holds
/// the chain advisory lock until it commits, so it contains the insert alone
/// and commits immediately; every heavy read happens before it opens.
/// </remarks>
public interface IAuditDisclosureTrail
{
    /// <summary>
    /// Appends the disclosure in a transaction of its own and commits it. One
    /// answer can touch more than one subject, and each subject earns a link of
    /// its own so "who looked at this afterwards" stays a read by subject; the
    /// links share one transaction, so they also share one acquisition of the
    /// chain lock. Throws when the append fails, and the caller must turn that
    /// into a refusal, never into a partial answer.
    /// </summary>
    Task RecordAsync(IReadOnlyCollection<AuditEntry> entries, CancellationToken cancellationToken);
}
