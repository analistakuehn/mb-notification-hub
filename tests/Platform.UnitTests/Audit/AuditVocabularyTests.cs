using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.UnitTests.Audit;

/// <summary>
/// The audit vocabulary is a published contract: stored rows reference these
/// exact strings forever, so any drift breaks reconstruction of past events.
/// </summary>
public sealed class AuditVocabularyTests
{
    [Fact]
    public void Actor_types_keep_their_canonical_strings()
    {
        AuditActorTypes.User.ShouldBe("user");
        AuditActorTypes.System.ShouldBe("system");
    }

    [Fact]
    public void Entity_types_keep_their_canonical_strings()
    {
        AuditEntityTypes.Template.ShouldBe("template");
        AuditEntityTypes.TemplateVersion.ShouldBe("template_version");
        AuditEntityTypes.Layout.ShouldBe("layout");
        AuditEntityTypes.LayoutVersion.ShouldBe("layout_version");
        AuditEntityTypes.ClassPolicyVersion.ShouldBe("class_policy_version");
    }

    [Fact]
    public void Actions_keep_their_canonical_strings()
    {
        AuditActions.TemplateCreated.ShouldBe("template.created");
        AuditActions.TemplateVersionPublished.ShouldBe("template.version.published");
        AuditActions.TemplateDeprecated.ShouldBe("template.deprecated");
        AuditActions.TemplateDisabled.ShouldBe("template.disabled");
        AuditActions.TemplateRollback.ShouldBe("template.rollback");
        AuditActions.LayoutCreated.ShouldBe("layout.created");
        AuditActions.LayoutVersionPublished.ShouldBe("layout.version.published");
        AuditActions.LayoutDeprecated.ShouldBe("layout.deprecated");
        AuditActions.LayoutDisabled.ShouldBe("layout.disabled");
        AuditActions.LayoutRollback.ShouldBe("layout.rollback");
        AuditActions.ClassPolicyVersionPublished.ShouldBe("class_policy.version.published");
    }

    [Fact]
    public void Approval_subjects_and_roles_keep_their_canonical_strings()
    {
        ApprovalSubjectTypes.TemplateVersion.ShouldBe("template_version");
        ApprovalSubjectTypes.LayoutVersion.ShouldBe("layout_version");
        ApprovalSubjectTypes.ClassPolicyVersion.ShouldBe("class_policy_version");
        ApprovalRoles.Publisher.ShouldBe("publisher");
    }
}
