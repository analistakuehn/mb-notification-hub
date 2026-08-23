namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Canonical actor types of an audit event.</summary>
public static class AuditActorTypes
{
    public const string User = "user";
    public const string System = "system";
}

/// <summary>Canonical entity types this module audits.</summary>
public static class AuditEntityTypes
{
    public const string Template = "template";
    public const string TemplateVersion = "template_version";
    public const string Layout = "layout";
    public const string LayoutVersion = "layout_version";
    public const string ClassPolicyVersion = "class_policy_version";
}

/// <summary>Canonical action names this module records.</summary>
public static class AuditActions
{
    public const string TemplateCreated = "template.created";
    public const string TemplateVersionPublished = "template.version.published";
    public const string TemplateDeprecated = "template.deprecated";
    public const string TemplateDisabled = "template.disabled";
    public const string TemplateRollback = "template.rollback";
    public const string LayoutCreated = "layout.created";
    public const string LayoutVersionPublished = "layout.version.published";
    public const string LayoutDeprecated = "layout.deprecated";
    public const string LayoutDisabled = "layout.disabled";
    public const string LayoutRollback = "layout.rollback";
    public const string ClassPolicyVersionPublished = "class_policy.version.published";
}

/// <summary>Everything an audit event must capture about one governed effect.</summary>
public sealed record AuditEntry
{
    public required string ActorType { get; init; }

    /// <summary>Stable identity-provider id (oid/appid) of whoever caused the effect.</summary>
    public required string ActorId { get; init; }

    public string? Application { get; init; }

    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    /// <summary>Compact JSON document with the action-specific evidence. Never personal data.</summary>
    public required string DetailsJson { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }
}

/// <summary>
/// One row of the transactional audit trail. The event is inserted in the same
/// database transaction as the effect it records: no effect without its trail.
/// Rows are append-only by construction; the database rejects updates and
/// deletes. The sequence number is assigned by the store on insert.
/// </summary>
public sealed class AuditEvent
{
    private AuditEvent(AuditEntry entry)
    {
        Id = Guid.CreateVersion7();
        OccurredAt = entry.OccurredAt;
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
