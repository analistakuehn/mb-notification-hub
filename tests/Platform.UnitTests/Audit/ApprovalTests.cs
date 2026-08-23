using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.UnitTests.Audit;

public sealed class ApprovalTests
{
    private static readonly DateTimeOffset ApprovedAt = new(2026, 8, 22, 15, 0, 0, TimeSpan.Zero);
    private static readonly string ContentHash = new('a', 64);

    [Fact]
    public void A_publication_approval_binds_the_approver_to_the_exact_content_hash()
    {
        var approval = Approval.Grant(Grant());

        approval.Id.ShouldNotBe(Guid.Empty);
        approval.SubjectType.ShouldBe("template_version");
        approval.SubjectId.ShouldBe("auth.otp.login");
        approval.SubjectVersion.ShouldBe(3);
        approval.ContentHash.ShouldBe(ContentHash);
        approval.Role.ShouldBe("publisher");
        approval.ApproverOid.ShouldBe("publisher-1");
        approval.ApprovedAt.ShouldBe(ApprovedAt);
    }

    [Fact]
    public void A_class_policy_approval_carries_the_application_class_pair_composed_by_the_caller()
    {
        var approval = Approval.Grant(Grant() with
        {
            SubjectType = ApprovalSubjectTypes.ClassPolicyVersion,
            SubjectId = "araia-cambio:critical",
            SubjectVersion = 2,
            ApproverOid = "publisher-2",
        });

        approval.SubjectType.ShouldBe("class_policy_version");
        approval.SubjectId.ShouldBe("araia-cambio:critical");
        approval.SubjectVersion.ShouldBe(2);
        approval.ContentHash.ShouldBe(ContentHash);
        approval.ApproverOid.ShouldBe("publisher-2");
    }

    [Fact]
    public void An_approval_requires_a_content_hash()
        => Should.Throw<ArgumentException>(() => Approval.Grant(Grant() with { ContentHash = " " }));

    [Fact]
    public void An_approval_requires_an_approver_identity()
        => Should.Throw<ArgumentException>(() => Approval.Grant(Grant() with { ApproverOid = " " }));

    [Fact]
    public void An_approval_requires_a_role()
        => Should.Throw<ArgumentException>(() => Approval.Grant(Grant() with { Role = " " }));

    [Fact]
    public void An_approval_requires_a_positive_subject_version()
        => Should.Throw<ArgumentOutOfRangeException>(() => Approval.Grant(Grant() with { SubjectVersion = 0 }));

    private static ApprovalGrant Grant() => new()
    {
        SubjectType = ApprovalSubjectTypes.TemplateVersion,
        SubjectId = "auth.otp.login",
        SubjectVersion = 3,
        ContentHash = ContentHash,
        Role = ApprovalRoles.Publisher,
        ApproverOid = "publisher-1",
        ApprovedAt = ApprovedAt,
    };
}
