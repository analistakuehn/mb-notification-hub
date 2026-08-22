using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class ApprovalTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;
    private static readonly DateTimeOffset ApprovedAt = new(2026, 8, 22, 15, 0, 0, TimeSpan.Zero);
    private static readonly string ContentHash = new('a', 64);

    [Fact]
    public void A_publication_approval_binds_the_approver_to_the_exact_content_hash()
    {
        Approval approval = Approval.ForTemplateVersion(Key, 3, ContentHash, "publisher-1", ApprovedAt);

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
    public void An_approval_requires_a_content_hash()
        => Should.Throw<ArgumentException>(
            () => Approval.ForTemplateVersion(Key, 1, " ", "publisher-1", ApprovedAt));

    [Fact]
    public void An_approval_requires_an_approver_identity()
        => Should.Throw<ArgumentException>(
            () => Approval.ForTemplateVersion(Key, 1, ContentHash, " ", ApprovedAt));

    [Fact]
    public void An_approval_requires_a_positive_subject_version()
        => Should.Throw<ArgumentOutOfRangeException>(
            () => Approval.ForTemplateVersion(Key, 0, ContentHash, "publisher-1", ApprovedAt));
}
