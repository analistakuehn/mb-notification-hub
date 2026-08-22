using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.login").Value!;

    [Fact]
    public void A_new_template_starts_active_with_its_metadata_preserved()
    {
        Result<Template> result = Template.Create(Key, new TemplateMetadata(
            "araia-cambio",
            NotificationClass.Critical,
            "identity-squad",
            "authentication",
            "execucao-de-contrato"));

        result.IsSuccess.ShouldBeTrue();
        Template template = result.Value!;
        template.Key.Value.ShouldBe("auth.otp.login");
        template.Application.ShouldBe("araia-cambio");
        template.Class.ShouldBe(NotificationClass.Critical);
        template.OwnerTeam.ShouldBe("identity-squad");
        template.Purpose.ShouldBe("authentication");
        template.LegalBasis.ShouldBe("execucao-de-contrato");
        template.Status.ShouldBe(TemplateStatus.Active);
    }

    [Fact]
    public void Metadata_text_fields_are_trimmed()
    {
        Result<Template> result = Template.Create(Key, new TemplateMetadata(
            " araia-cambio ",
            NotificationClass.Operational,
            " ops ",
            " reminders ",
            " legitimo-interesse "));

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Application.ShouldBe("araia-cambio");
        result.Value!.OwnerTeam.ShouldBe("ops");
    }

    [Theory]
    [InlineData("Araia-Cambio")]
    [InlineData("araia cambio")]
    [InlineData("")]
    [InlineData("araia_cambio")]
    public void Rejects_applications_outside_the_naming_convention(string application)
    {
        Result<Template> result = Template.Create(Key, new TemplateMetadata(
            application,
            NotificationClass.Critical,
            "identity-squad",
            "authentication",
            "execucao-de-contrato"));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Theory]
    [InlineData("", "authentication", "legal")]
    [InlineData("owners", "", "legal")]
    [InlineData("owners", "authentication", "")]
    public void Rejects_blank_governance_fields(string ownerTeam, string purpose, string legalBasis)
    {
        Result<Template> result = Template.Create(Key, new TemplateMetadata(
            "araia-cambio",
            NotificationClass.Critical,
            ownerTeam,
            purpose,
            legalBasis));

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }
}
