namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authorization;

/// <summary>One grant of the registry as the snapshot indexes it.</summary>
internal readonly record struct ProducerGrant(string Principal, string Application, string Class);

/// <summary>
/// One short-lived snapshot of the producer registry: the set of grants the
/// hub read from the materialized table, and when it read them. The grant is
/// a tuple, never a composed string, so no separator can ever be smuggled
/// inside a principal or an application name.
/// </summary>
internal sealed class ProducerGrants(IReadOnlySet<ProducerGrant> grants, DateTimeOffset loadedAt)
{
    public DateTimeOffset LoadedAt { get; } = loadedAt;

    /// <summary>How many grants the snapshot holds; reported by the readiness surface.</summary>
    public int Count => grants.Count;

    /// <summary>
    /// An empty snapshot is never treated as "nobody is authorized": it is
    /// indistinguishable from a materialization job that never ran, and the
    /// bus retention is short enough that a day of legitimate traffic would
    /// reach the dead-letter topic before anyone noticed.
    /// </summary>
    public bool IsEmpty => grants.Count == 0;

    public bool Allows(string principal, string application, string canonicalClass)
        => grants.Contains(new ProducerGrant(principal, application, canonicalClass));
}

/// <summary>
/// Read side of the producer registry. The registry is the hub-side half of
/// the two-layer authorization of the bus: the broker ACL decides who writes
/// to the topic, this table decides which application and class each principal
/// may request. Its canonical form lives in the infrastructure repository.
/// </summary>
internal interface IProducerRegistry
{
    /// <summary>
    /// The current snapshot, refreshed when stale. Null only when the table
    /// was never read successfully; a failed refresh with an older snapshot
    /// keeps serving the older one, because authorization must not stop while
    /// the table is briefly unreachable.
    /// </summary>
    Task<ProducerGrants?> CurrentAsync(CancellationToken cancellationToken);
}
