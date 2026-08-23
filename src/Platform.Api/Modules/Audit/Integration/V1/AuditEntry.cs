namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>Canonical actor types of an audit event.</summary>
public static class AuditActorTypes
{
    public const string User = "user";
    public const string System = "system";
}

/// <summary>Canonical entity types the audit trail records.</summary>
public static class AuditEntityTypes
{
    public const string Template = "template";
    public const string TemplateVersion = "template_version";
    public const string Layout = "layout";
    public const string LayoutVersion = "layout_version";
    public const string ClassPolicyVersion = "class_policy_version";

    /// <summary>One monthly partition of the trail, named as the store names it.</summary>
    public const string AuditPartition = "audit_partition";
}

/// <summary>Canonical action names of the audit vocabulary.</summary>
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

    /// <summary>A verification round closed with the chain intact over the range it covered.</summary>
    public const string AuditChainVerified = "audit.chain.verified";

    /// <summary>A verification round found a link that does not hold; the range and the reason travel in the details.</summary>
    public const string AuditChainVerificationFailed = "audit.chain.verification_failed";

    /// <summary>Evidence of one partition slice written to the immutable store.</summary>
    public const string AuditExported = "audit.exported";

    /// <summary>A partition finished its closing cycle: verified, exported, copy checked, detached.</summary>
    public const string AuditPartitionClosed = "audit.partition.closed";
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
