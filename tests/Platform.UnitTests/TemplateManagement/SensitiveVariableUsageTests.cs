using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The declaration that no longer matches the content. A schema and a body that
/// rename a variable while the declaration still points at the old name publish
/// green and store the value in clear, and the trail keeps check names without
/// their messages, so under the generic unused-variable warning that publication
/// is indistinguishable from a schema prepared for a locale that has not landed.
/// </summary>
public sealed class SensitiveVariableUsageTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("orders.status.changed").Value!;

    private const string Schema =
        """
        {
          "type": "object",
          "properties": {
            "documento": { "type": "string" },
            "cliente": {
              "type": "object",
              "properties": { "cpf": { "type": "string" } }
            }
          }
        }
        """;

    [Fact]
    public void A_declared_name_no_content_reads_is_named_by_a_warning_of_its_own()
    {
        ValidationReport report = Run(declared: ["documento"], body: "Seu CPF é {{ cliente.cpf }}.");

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.SensitiveVariablesUnused);
        check.Status.ShouldBe(ValidationCheckStatuses.Warning);
        check.Message.ShouldContain("'documento'");

        // A warning and never a failure: the same shape is an author preparing
        // a channel or a locale that has not landed yet.
        report.Passed.ShouldBeTrue();
    }

    /// <summary>
    /// The reason this reads the raw text instead of the variables the sandbox
    /// reports. The analyzer answers with the root identifier of a read, so
    /// <c>cliente.cpf</c> reaches it as <c>cliente</c>: built on that, a
    /// declaration of <c>cpf</c> would be warned about while the mask does
    /// reach the value, which is the false alarm that turns a warning into
    /// noise nobody reads.
    /// </summary>
    [Fact]
    public void A_bare_name_read_through_a_dotted_path_counts_as_read()
    {
        ValidationReport report = Run(declared: ["cpf"], body: "Seu CPF é {{ cliente.cpf }}.");

        report.Checks.Single(check => check.Name == ValidationCheckNames.SensitiveVariablesUnused)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    private static ValidationReport Run(IReadOnlyList<string> declared, string body)
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

        var version = TemplateVersion.CreateDraft(Key, 1, "author-1", DateTimeOffset.UtcNow);
        version.SetContent(
            new ContentEdit(
                Channel.Create("sms").Value!,
                Locale.Create("pt-BR").Value!,
                null,
                body,
                null),
            "author-1").IsSuccess.ShouldBeTrue();
        version.SetVariablesSchema(Schema, "author-1").IsSuccess.ShouldBeTrue();
        version.SetSensitiveVariables(declared, "author-1").IsSuccess.ShouldBeTrue();
        version.SensitiveVariables.ShouldBe(declared);

        return TemplateValidation.Validate(
            template,
            version,
            [
                new ContentAnalysis(
                    Channel.Create("sms").Value!,
                    Locale.Create("pt-BR").Value!,
                    [new ContentFieldAnalysis("body", true, null, ["cliente"], [])]),
            ]);
    }
}
