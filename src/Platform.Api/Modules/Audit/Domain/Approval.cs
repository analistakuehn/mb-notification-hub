using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// Proof of publication: who approved which exact content, when, under which
/// role. The approval binds the approver to the content hash of an immutable
/// version, so approving one text and publishing another is impossible. Rows
/// are append-only; an approval is never edited or revoked. Subject identity
/// arrives already composed in the owning context's naming, because this
/// module records proof without modeling foreign aggregates.
/// </summary>
public sealed class Approval
{
    private Approval(ApprovalGrant grant)
    {
        Id = Guid.CreateVersion7();
        SubjectType = grant.SubjectType;
        SubjectId = grant.SubjectId;
        SubjectVersion = grant.SubjectVersion;
        ContentHash = grant.ContentHash;
        Role = grant.Role;
        ApproverOid = grant.ApproverOid;
        ApprovedAt = grant.ApprovedAt;
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

    public static Approval Grant(ApprovalGrant grant)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.SubjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.SubjectId);
        ArgumentOutOfRangeException.ThrowIfLessThan(grant.SubjectVersion, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.ContentHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.Role);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.ApproverOid);
        return new Approval(grant);
    }
}
