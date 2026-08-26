using System.Diagnostics;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The sensitive-variable check, at the two points it has to hold: a name that
/// would never mask must not reach publication, and a field dense in URL
/// schemes must not cost the authoring endpoint a core.
/// </summary>
public sealed class SensitiveVariableValidationTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("orders.status.changed").Value!;

    private const string Schema =
        """{ "type": "object", "properties": { "cpf": { "type": "string" }, "cliente": { "type": "object" } } }""";

    [Fact]
    public void A_sensitive_variable_the_schema_never_declares_fails_the_check()
    {
        // The mask only walks the payload's top level, so a name the schema
        // does not declare masks nothing, and the render stores the full form
        // as if it were the masked one.
        ValidationCheck check = Run(sensitive: ["documento"], body: "Seu CPF é {{ cpf }}.");

        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("documento");
        check.Message.ShouldContain("never be masked");
    }

    [Fact]
    public void A_nested_sensitive_name_fails_because_masking_never_reaches_it()
    {
        // The exact leak: 'cpf' nested inside 'cliente' reads fine in the body,
        // and the mask still never touches it.
        ValidationCheck check = Run(sensitive: ["cpf_do_cliente"], body: "Seu CPF é {{ cliente.cpf }}.");

        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
    }

    [Fact]
    public void A_declared_sensitive_variable_outside_a_url_passes()
    {
        ValidationCheck check = Run(sensitive: ["cpf"], body: "Seu CPF é {{ cpf }}.");

        check.Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void A_declared_sensitive_variable_inside_a_url_still_fails()
    {
        ValidationCheck check = Run(
            sensitive: ["cpf"],
            body: "Acesse https://exemplo.com/cliente/{{ cpf }}/extrato");

        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("URL position");
    }

    [Fact]
    public void A_sensitive_variable_beside_a_url_but_outside_it_passes()
    {
        ValidationCheck check = Run(
            sensitive: ["cpf"],
            body: "Acesse https://exemplo.com/extrato com o CPF {{ cpf }}.");

        check.Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void A_template_declaring_no_sensitive_variable_passes()
    {
        ValidationCheck check = Run(sensitive: [], body: "Olá, tudo bem?");

        check.Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    // The two cases below are falsified by behavior, not by a stopwatch. The
    // previous expressions backtracked quadratically over exactly these inputs
    // and aborted as `RegexMatchTimeoutException`, which nothing on either path
    // caught: the assertion could not even be reached, because the call threw.
    // A wall-clock budget would be the weaker oracle and a flaky one, since it
    // fails on a loaded machine for reasons that have nothing to do with the
    // code. The generous ceiling that remains guards against a hang, and is not
    // a performance claim.
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(20);

    [Fact]
    public void A_field_dense_in_url_schemes_validates_without_aborting()
    {
        var body = string.Concat(Enumerable.Repeat("http://", 60_000));

        var watch = Stopwatch.StartNew();
        ValidationCheck check = Run(sensitive: ["cpf"], body: body);
        watch.Stop();

        check.Status.ShouldBe(ValidationCheckStatuses.Passed);
        watch.Elapsed.ShouldBeLessThan(HangGuard);
    }

    [Fact]
    public void A_link_like_scan_over_a_long_dotted_run_completes()
    {
        // This predicate also runs at dispatch, inside a contract whose consumer
        // does not handle exceptions, where an escaping timeout became a poison
        // message redelivered until the redrive policy gave up.
        var text = string.Concat(Enumerable.Repeat("a.", 100_000));

        var watch = Stopwatch.StartNew();
        var carriesLink = TemplateValidation.ContainsLinkLikeText(text);
        watch.Stop();

        carriesLink.ShouldBeFalse();
        watch.Elapsed.ShouldBeLessThan(HangGuard);
    }

    [Fact]
    public void The_link_detector_still_recognizes_what_it_is_for()
    {
        TemplateValidation.ContainsLinkLikeText("seu código em bit.ly/x9k2p").ShouldBeTrue();
        TemplateValidation.ContainsLinkLikeText("acesse www.exemplo.com").ShouldBeTrue();
        TemplateValidation.ContainsLinkLikeText("acesse https://exemplo.com").ShouldBeTrue();
        TemplateValidation.ContainsLinkLikeText("seu código é 998877").ShouldBeFalse();
    }

    private static ValidationCheck Run(IReadOnlyList<string> sensitive, string body)
    {
        Template template = Template.Create(Key, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
            LinkDomainsAllowed = ["exemplo.com"],
            SensitiveVariables = sensitive,
        }).Value!;

        var version = TemplateVersion.CreateDraft(Key, 1, "author-1", DateTimeOffset.UtcNow);
        version.SetContent(
            new ContentEdit(
                Channel.Create("email").Value!,
                Locale.Create("pt-BR").Value!,
                "Aviso",
                body,
                null),
            "author-1").IsSuccess.ShouldBeTrue();
        version.SetVariablesSchema(Schema, "author-1").IsSuccess.ShouldBeTrue();

        ValidationReport report = TemplateValidation.Validate(template, version, [
            new ContentAnalysis(
                Channel.Create("email").Value!,
                Locale.Create("pt-BR").Value!,
                [
                    new ContentFieldAnalysis("subject", true, null, []),
                    new ContentFieldAnalysis("body", true, null, ["cpf", "cliente"]),
                ]),
        ]);

        return report.Checks.First(check => check.Name == ValidationCheckNames.SensitiveVariables);
    }
}
