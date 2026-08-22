namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>Canonical subject types an approval can cover.</summary>
public static class ApprovalSubjectTypes
{
    public const string TemplateVersion = "template_version";
}

/// <summary>Canonical roles under which an approval is granted.</summary>
public static class ApprovalRoles
{
    public const string Publisher = "publisher";
}

/// <summary>
/// Proof of publication: who approved which exact content, when, under which
/// role. The approval binds the approver to the content hash of an immutable
/// version, so approving one text and publishing another is impossible. Rows
/// are append-only; an approval is never edited or revoked.
/// </summary>
public sealed class Approval
{
    private Approval(ApprovalSubject subject, string role, string approverOid, DateTimeOffset approvedAt)
    {
        Id = Guid.CreateVersion7();
        SubjectType = subject.Type;
        SubjectId = subject.Id;
        SubjectVersion = subject.Version;
        ContentHash = subject.ContentHash;
        Role = role;
        ApproverOid = approverOid;
        ApprovedAt = approvedAt;
    }

    // EF Core materialization: fields are populated from the store.
    private Approval()
    {
        SubjectType = null!;
        SubjectId = null!;
        ContentHash = null!;
        Role = null!;
        ApproverOid = null!;
    }

    public Guid Id { get; }

    public string SubjectType { get; }

    public string SubjectId { get; }

    public int SubjectVersion { get; }

    /// <summary>Hash of the exact content the approver vouched for.</summary>
    public string ContentHash { get; }

    public string Role { get; }

    /// <summary>Stable identity-provider object id of the approver.</summary>
    public string ApproverOid { get; }

    public DateTimeOffset ApprovedAt { get; }

    public static Approval ForTemplateVersion(
        TemplateKey templateKey,
        int version,
        string contentHash,
        string approverOid,
        DateTimeOffset approvedAt)
    {
        ArgumentNullException.ThrowIfNull(templateKey);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(approverOid);

        var subject = new ApprovalSubject(ApprovalSubjectTypes.TemplateVersion, templateKey.Value, version, contentHash);
        return new Approval(subject, ApprovalRoles.Publisher, approverOid, approvedAt);
    }

    private sealed record ApprovalSubject(string Type, string Id, int Version, string ContentHash);
}
