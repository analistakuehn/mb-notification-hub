using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateLifecycleTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;

    [Fact]
    public void An_active_template_can_be_deprecated()
    {
        Template template = NewTemplate();

        Result result = template.Deprecate();

        result.IsSuccess.ShouldBeTrue();
        template.Status.ShouldBe(TemplateStatus.Deprecated);
    }

    [Fact]
    public void An_active_template_can_be_disabled_directly()
    {
        Template template = NewTemplate();

        Result result = template.Disable();

        result.IsSuccess.ShouldBeTrue();
        template.Status.ShouldBe(TemplateStatus.Disabled);
    }

    [Fact]
    public void A_deprecated_template_can_still_be_disabled()
    {
        Template template = NewTemplate();
        template.Deprecate().IsSuccess.ShouldBeTrue();

        Result result = template.Disable();

        result.IsSuccess.ShouldBeTrue();
        template.Status.ShouldBe(TemplateStatus.Disabled);
    }

    [Fact]
    public void Deprecating_twice_names_the_current_status_and_the_remaining_transitions()
    {
        Template template = NewTemplate();
        template.Deprecate().IsSuccess.ShouldBeTrue();

        Result result = template.Deprecate();

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe("deprecated");
        info.AllowedTransitions.ShouldBe(["disabled"]);
        template.Status.ShouldBe(TemplateStatus.Deprecated);
    }

    [Fact]
    public void Disabling_a_disabled_template_is_rejected_with_no_transitions_left()
    {
        Template template = NewTemplate();
        template.Disable().IsSuccess.ShouldBeTrue();

        Result result = template.Disable();

        result.IsFailure.ShouldBeTrue();
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe("disabled");
        info.AllowedTransitions.ShouldBeEmpty();
    }

    [Fact]
    public void An_active_template_accepts_publications()
    {
        Template template = NewTemplate();

        template.EnsureAcceptsPublication().IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData(false, "deprecated")]
    [InlineData(true, "disabled")]
    public void A_retired_template_rejects_publications_naming_its_status(bool disable, string expectedStatus)
    {
        Template template = NewTemplate();
        Result transition = disable ? template.Disable() : template.Deprecate();
        transition.IsSuccess.ShouldBeTrue();

        Result result = template.EnsureAcceptsPublication();

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.BusinessRule);
        DomainErrorInfo info = DomainError.Describe(result.Error, result.ErrorKind);
        info.Code.ShouldBe(ErrorCodes.InvalidStateTransition);
        info.CurrentStatus.ShouldBe(expectedStatus);
    }

    private static Template NewTemplate()
        => Template.Create(Key, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Critical,
            OwnerTeam = "identity-squad",
            Purpose = "authentication",
            LegalBasis = "execucao-de-contrato",
        }).Value!;
}
