using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// A link inside an authentication SMS is the phishing vector this catalog
/// exists to refuse. The cost of a false positive is one authentication code;
/// the cost of a false negative is the one message people are trained to act
/// on without reading twice.
/// </summary>
public sealed class AuthenticationSmsLinkValidationTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("auth.otp.requested").Value!;

    [Theory]
    [InlineData("Acesse https://banco.example.com/entrar com o código 123456.")]
    [InlineData("Confirme em www.banco-example.com e informe 123456.")]
    [InlineData("Seu código é 123456. Detalhes em bit.ly/x9k2p")]
    [InlineData("Toque em banco-example.com.br/otp para liberar.")]
    public void Every_shape_of_a_link_is_recognized(string text)
        => TemplateValidation.ContainsLinkLikeText(text).ShouldBeTrue();

    [Theory]
    [InlineData("Seu código de acesso é 123456. Válido por 5 minutos.")]
    [InlineData("Não compartilhe este código com ninguém, nem com o banco.")]
    [InlineData("Código 123456 para a transferência de R$ 1.234,56.")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_authentication_text_is_not_a_link(string? text)
        => TemplateValidation.ContainsLinkLikeText(text).ShouldBeFalse();

    [Fact]
    public void A_link_in_the_sms_content_of_an_authentication_template_blocks_publication()
    {
        Template template = Authentication(linkDomains: ["banco.example.com"]);
        TemplateVersion version = MakeVersion(
            ("sms", "pt-BR", "Código {{ code }}. Confirme em https://banco.example.com/otp"));

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("sms", "pt-BR", ("body", ["code"])),
        ]);

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.AuthenticationSmsLinks);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Location.ShouldBe("sms/pt-BR/body");
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void An_allowed_link_domain_does_not_buy_a_link_in_an_authentication_sms()
    {
        // The allowlist answers "which domains may this template link to". It
        // never answers "may this message carry a link at all", which is the
        // question this rule owns.
        Template template = Authentication(linkDomains: ["banco.example.com"]);
        TemplateVersion version = MakeVersion(
            ("sms", "pt-BR", "Confirme em https://banco.example.com/otp"));

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("sms", "pt-BR", ("body", [])),
        ]);

        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.UrlAllowlist)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.AuthenticationSmsLinks)
            .Status.ShouldBe(ValidationCheckStatuses.Failed);
    }

    [Fact]
    public void A_url_variable_used_by_the_sms_content_blocks_publication_too()
    {
        // The source stays clean and the address still reaches the person.
        Template template = Authentication(linkDomains: ["banco.example.com"]);
        TemplateVersion version = MakeVersion(("sms", "pt-BR", "Código {{ code }}: {{ link }}"));
        version.SetVariablesSchema(
            """
            {
              "type": "object",
              "properties": {
                "code": { "type": "string" },
                "link": { "type": "string", "format": "uri" }
              }
            }
            """,
            "author-1").IsSuccess.ShouldBeTrue();

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("sms", "pt-BR", ("body", ["code", "link"])),
        ]);

        report.Checks.Count(candidate =>
                candidate.Name == ValidationCheckNames.AuthenticationSmsLinks
                && candidate.Status == ValidationCheckStatuses.Failed)
            .ShouldBe(1);
    }

    [Fact]
    public void Clean_authentication_sms_content_passes_the_check()
    {
        Template template = Authentication();
        TemplateVersion version = MakeVersion(
            ("sms", "pt-BR", "Seu código de acesso é {{ code }}. Não compartilhe."));

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("sms", "pt-BR", ("body", ["code"])),
        ]);

        report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.AuthenticationSmsLinks)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void The_rule_applies_to_the_sms_content_and_to_no_other_channel()
    {
        // Push and e-mail of the same authentication template keep their links:
        // what makes SMS different is that the message arrives outside the app,
        // with no sender identity the person can check.
        Template template = Authentication(linkDomains: ["banco.example.com"]);
        TemplateVersion version = MakeVersion(
            ("push", "pt-BR", "Confirme em https://banco.example.com/otp"));

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("push", "pt-BR", ("body", [])),
        ]);

        report.Checks.ShouldNotContain(candidate =>
            candidate.Name == ValidationCheckNames.AuthenticationSmsLinks);
    }

    [Fact]
    public void A_template_of_another_purpose_keeps_its_links_in_sms()
    {
        // Falsification: the purpose is what triggers the rule, not the
        // channel alone. A marketing SMS with an allowed link stays publishable.
        Template template = Template.Create(Key, Metadata("order-updates", ["loja.example.com"])).Value!;
        TemplateVersion version = MakeVersion(
            ("sms", "pt-BR", "Acompanhe em https://loja.example.com/pedido"));

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("sms", "pt-BR", ("body", [])),
        ]);

        report.Checks.ShouldNotContain(candidate =>
            candidate.Name == ValidationCheckNames.AuthenticationSmsLinks);
        report.Passed.ShouldBeTrue();
    }

    private static Template Authentication(IReadOnlyList<string>? linkDomains = null)
        => Template.Create(
            Key,
            Metadata(TemplateValidation.AuthenticationPurpose, linkDomains ?? [])).Value!;

    private static TemplateMetadata Metadata(string purpose, IReadOnlyList<string> linkDomains)
        => new()
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = purpose,
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = Locale.Create("pt-BR").Value,
            LinkDomainsAllowed = linkDomains,
            SensitiveVariables = [],
        };

    private static TemplateVersion MakeVersion(
        params (string Channel, string Locale, string Body)[] contents)
    {
        var version = TemplateVersion.CreateDraft(Key, 1, "author-1", DateTimeOffset.UtcNow);
        foreach ((var channel, var locale, var body) in contents)
        {
            version.SetContent(
                new ContentEdit(
                    Channel.Create(channel).Value!,
                    Locale.Create(locale).Value!,
                    channel == "push" ? "Título" : null,
                    body,
                    null),
                "author-1").IsSuccess.ShouldBeTrue();
        }

        return version;
    }

    private static ContentAnalysis Analysis(
        string channel,
        string locale,
        params (string Field, string[] Used)[] fields)
        => new(
            Channel.Create(channel).Value!,
            Locale.Create(locale).Value!,
            fields
                .Select(field => new ContentFieldAnalysis(field.Field, true, null, field.Used))
                .ToList());
}
