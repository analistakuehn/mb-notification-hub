namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>Canonical subject types an approval can cover.</summary>
public static class ApprovalSubjectTypes
{
    public const string TemplateVersion = "template_version";
    public const string LayoutVersion = "layout_version";
    public const string ClassPolicyVersion = "class_policy_version";
}

/// <summary>Canonical roles under which an approval is granted.</summary>
public static class ApprovalRoles
{
    public const string Publisher = "publisher";
}

/// <summary>
/// Everything an approval must capture: who approved which exact content, when,
/// under which role. The content hash binds the approver to an immutable
/// version, so approving one text and publishing another is impossible.
/// </summary>
public sealed record ApprovalGrant
{
    public required string SubjectType { get; init; }

    /// <summary>Identity of the approved subject in its owning context's naming (for example a template key).</summary>
    public required string SubjectId { get; init; }

    public required int SubjectVersion { get; init; }

    /// <summary>Hash of the exact content the approver vouched for.</summary>
    public required string ContentHash { get; init; }

    public required string Role { get; init; }

    /// <summary>Stable identity-provider object id of the approver.</summary>
    public required string ApproverOid { get; init; }

    public required DateTimeOffset ApprovedAt { get; init; }
}
