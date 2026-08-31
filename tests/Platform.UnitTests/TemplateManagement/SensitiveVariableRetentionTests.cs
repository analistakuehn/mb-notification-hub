using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Whether a template accepts ingestion over the bus is read off the sensitive
/// declaration of the published version, and the render masks exactly what that
/// declaration names. Both properties therefore change the moment a new version
/// takes over, and a version that drops a name changes them without saying so.
/// </summary>
public sealed class SensitiveVariableRetentionTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("orders.status.changed").Value!;

    private const string Schema =
        """{ "type": "object", "properties": { "cpf": { "type": "string" }, "email": { "type": "string" } } }""";

    [Fact]
    public void A_version_that_drops_a_sensitive_variable_in_force_is_refused()
    {
        ValidationReport report = Run(declared: ["email"], inForce: ["cpf", "email"]);

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.SensitiveVariablesRetained);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'cpf'");
        report.Passed.ShouldBeFalse();
    }

    /// <summary>
    /// The positive half. Without it the negative one is satisfied by a check
    /// that refuses every version, and the report would be a constant.
    /// </summary>
    [Fact]
    public void A_version_that_keeps_every_sensitive_variable_in_force_is_accepted()
    {
        ValidationReport report = Run(declared: ["cpf", "email"], inForce: ["cpf"]);

        report.Checks.Single(check => check.Name == ValidationCheckNames.SensitiveVariablesRetained)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
        report.Passed.ShouldBeTrue();
    }

    /// <summary>
    /// A first publication has no version in force, so there is nothing to
    /// regress from and the rule stays out of the report entirely rather than
    /// claiming a comparison it never made.
    /// </summary>
    [Fact]
    public void A_first_publication_reports_no_retention_verdict_at_all()
    {
        ValidationReport report = Run(declared: [], inForce: null);

        report.Checks.ShouldNotContain(check =>
            check.Name == ValidationCheckNames.SensitiveVariablesRetained);
    }

    private static ValidationReport Run(IReadOnlyList<string> declared, IReadOnlyList<string>? inForce)
    {
        Template template = Template.Create(Key, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
        }).Value!;

        var version = TemplateVersion.CreateDraft(Key, 2, "author-1", DateTimeOffset.UtcNow);
        version.SetContent(
            new ContentEdit(
                Channel.Create("email").Value!,
                Locale.Create("pt-BR").Value!,
                "Aviso",
                "<p>Seu CPF é {{ cpf }} e o contato é {{ email }}.</p>",
                "Seu CPF é {{ cpf }} e o contato é {{ email }}."),
            "author-1").IsSuccess.ShouldBeTrue();
        version.SetVariablesSchema(Schema, "author-1").IsSuccess.ShouldBeTrue();
        version.SetSensitiveVariables(declared, "author-1").IsSuccess.ShouldBeTrue();
        version.SensitiveVariables.ShouldBe(declared);

        return TemplateValidation.Validate(
            template,
            version,
            [
                new ContentAnalysis(
                    Channel.Create("email").Value!,
                    Locale.Create("pt-BR").Value!,
                    [
                        new ContentFieldAnalysis("body", true, null, ["cpf", "email"], []),
                        new ContentFieldAnalysis("bodyText", true, null, ["cpf", "email"], []),
                    ]),
            ],
            layoutReference: null,
            inForce);
    }
}
