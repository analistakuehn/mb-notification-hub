using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// One row of the transactional audit trail. The event is inserted in the same
/// database transaction as the effect it records: no effect without its trail.
/// Rows are append-only by construction; the database rejects updates and
/// deletes. The sequence number is assigned by the store on insert. Chained
/// rows carry the canonical bytes that were hashed plus the two chain hashes;
/// rows written before the chain existed keep the three columns absent, and
/// nothing is fabricated for them retroactively.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent(AuditEntry entry)
    {
        Id = Guid.CreateVersion7();

        // The store keeps timestamptz at microsecond precision. Truncating up
        // front makes the in-memory instant, the canonical text and the stored
        // column describe the same moment, byte for byte.
        OccurredAt = AuditChain.TruncateToMicroseconds(entry.OccurredAt);
        ActorType = entry.ActorType;
        ActorId = entry.ActorId;
        Application = entry.Application;
        Action = entry.Action;
        EntityType = entry.EntityType;
        EntityId = entry.EntityId;
        DetailsJson = entry.DetailsJson;
    }

    // EF Core materialization: fields are populated from the store.
    private AuditEvent()
    {
        ActorType = null!;
        ActorId = null!;
        Action = null!;
        EntityType = null!;
        EntityId = null!;
        DetailsJson = null!;
    }

    public Guid Id { get; }

    /// <summary>Store-assigned monotonic sequence within the table.</summary>
    public long Seq { get; }

    public DateTimeOffset OccurredAt { get; }

    public string ActorType { get; }

    public string ActorId { get; }

    public string? Application { get; }

    public string Action { get; }

    public string EntityType { get; }

    public string EntityId { get; }

    public string DetailsJson { get; }

    /// <summary>Exact UTF-8 text that was hashed into <see cref="Hash"/>; verification reads it, never the reserialized details.</summary>
    public string? Canonical { get; }

    /// <summary>Hash of the previous chained event of the monthly partition, or the partition anchor for the first one.</summary>
    public byte[]? PrevHash { get; }

    /// <summary>SHA-256 over <see cref="PrevHash"/> concatenated with the canonical bytes.</summary>
    public byte[]? Hash { get; }

    public static AuditEvent Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ActorType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.ActorId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.Action);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntityType);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.EntityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(entry.DetailsJson);
        return new AuditEvent(entry);
    }
}
