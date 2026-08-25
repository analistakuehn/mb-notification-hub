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

    /// <summary>
    /// One notification, identified as its producing context names it. A
    /// disclosure over a notification records this subject, so "who looked at
    /// this afterwards" is a read by subject and never a scan of the trail.
    /// </summary>
    public const string Notification = "notification";

    /// <summary>One recipient of the contact and consent source of truth.</summary>
    public const string Recipient = "recipient";

    /// <summary>One push registration of a recipient, as its owning context names it.</summary>
    public const string DeviceToken = "device_token";

    /// <summary>
    /// A switch that stops traffic, named as the context that owns it names
    /// the scope and the key it covers.
    /// </summary>
    public const string KillSwitch = "kill_switch";

    /// <summary>
    /// One object of recurring evidence in the immutable store, identified by
    /// the key it was archived under.
    /// </summary>
    public const string EvidenceObject = "evidence_object";
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

    /// <summary>
    /// Evidence about one subject left the hub through the audit surface. The
    /// details carry the route, the disclosed scope and the disclosed hashes,
    /// never a contact value and never a fragment of content.
    /// </summary>
    public const string AuditRead = "audit.read";

    /// <summary>
    /// A switch that stops traffic changed state. The actor may be a person or
    /// this platform itself, so a reader that assumes a human here is wrong;
    /// the actor type is what separates the two.
    /// </summary>
    public const string KillSwitchChanged = "kill_switch.changed";

    /// <summary>
    /// Recurring evidence was written to the immutable store. The details
    /// carry the key, the digest and the length, which is what lets an auditor
    /// tie the archived bytes to the moment they were archived.
    /// </summary>
    public const string EvidenceArchived = "evidence.archived";
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
