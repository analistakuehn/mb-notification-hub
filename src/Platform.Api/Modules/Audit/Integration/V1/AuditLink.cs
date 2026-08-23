using System.Text.Json;

namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>
/// One chained event of the trail, rebuilt from the canonical text that was
/// hashed. Every field below comes from parsing <see cref="Canonical"/>: the
/// scalar columns and the details column serve the query, never the proof. The
/// details column is jsonb and re-serializes on read, so a consumer that read
/// it would be quoting bytes no hash covers.
/// </summary>
public sealed record AuditLink
{
    /// <summary>Store-assigned sequence inside the trail; chain order within a partition.</summary>
    public required long Seq { get; init; }

    /// <summary>Lowercase hex of <c>SHA-256(prev_hash ‖ canonical)</c>.</summary>
    public required string Hash { get; init; }

    /// <summary>Lowercase hex of the predecessor's hash, or of the partition anchor for the first link.</summary>
    public required string PrevHash { get; init; }

    /// <summary>The exact UTF-8 text the hash covers, verbatim.</summary>
    public required string Canonical { get; init; }

    public required string Action { get; init; }

    public required string ActorType { get; init; }

    public required string ActorId { get; init; }

    public string? Application { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Action-specific evidence, parsed out of the canonical text.</summary>
    public required JsonElement Details { get; init; }
}

/// <summary>
/// The chained links of one subject inside one window, plus the number of rows
/// the window holds for that subject which carry no chain at all. Those rows
/// predate the chain and nothing was fabricated for them; declaring the count
/// keeps their absence visible instead of turning it into a silent hole.
/// </summary>
public sealed record AuditSubjectLinks
{
    public required IReadOnlyList<AuditLink> Links { get; init; }

    public required int UnchainedRows { get; init; }
}

/// <summary>
/// One approval of the governed catalog, as the trail recorded it. The table is
/// append-only but sits outside the hash chain, so an evidence consumer must
/// present it apart from the chained links.
/// </summary>
public sealed record ApprovalRecord
{
    public required string SubjectType { get; init; }

    public required string SubjectId { get; init; }

    public required int SubjectVersion { get; init; }

    public required string ContentHash { get; init; }

    public required string Role { get; init; }

    public required string ApproverOid { get; init; }

    public required DateTimeOffset ApprovedAt { get; init; }
}

/// <summary>Identity of one subject of the trail, in its producing context's naming.</summary>
public readonly record struct AuditSubject(string EntityType, string EntityId);
