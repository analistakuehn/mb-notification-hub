using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateValidationTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("orders.status.changed").Value!;

    [Fact]
    public void A_report_with_only_passed_and_warning_checks_passes()
    {
        var report = new ValidationReport([
            new ValidationCheck("a", ValidationCheckStatuses.Passed, "ok", null),
            new ValidationCheck("b", ValidationCheckStatuses.Warning, "heads up", null),
        ]);

        report.Passed.ShouldBeTrue();
    }

    [Fact]
    public void A_single_failed_check_fails_the_report()
    {
        var report = new ValidationReport([
            new ValidationCheck("a", ValidationCheckStatuses.Passed, "ok", null),
            new ValidationCheck("b", ValidationCheckStatuses.Failed, "broken", "email/pt-BR/body"),
        ]);

        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void Clean_content_produces_a_fully_passed_report()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(("email", "pt-BR", "Oi", "<p>Pedido {{ orderId }}</p>", "Pedido {{ orderId }}"));
        SetSchema(version, """{ "type": "object", "properties": { "orderId": { "type": "string" } } }""");

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("email", "pt-BR", ("subject", []), ("body", ["orderId"]), ("bodyText", ["orderId"])),
        ]);

        report.Passed.ShouldBeTrue();
        report.Checks.Select(check => check.Name).ShouldBe([
            ValidationCheckNames.Compilation,
            ValidationCheckNames.VariablesSchema,
            ValidationCheckNames.VariablesDeclared,
            ValidationCheckNames.VariablesUsed,
            ValidationCheckNames.UrlAllowlist,
            ValidationCheckNames.SensitiveVariables,
            ValidationCheckNames.ChannelLimits,
            ValidationCheckNames.DefaultLocale,
        ]);
        report.Checks.ShouldAllBe(check => check.Status == ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void A_parse_error_fails_the_compilation_check_at_its_location()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(("sms", "pt", null, "Código {{ code", null));

        ValidationReport report = TemplateValidation.Validate(template, version, [
            new ContentAnalysis(
                Channel.Create("sms").Value!,
                Locale.Create("pt").Value!,
                [new ContentFieldAnalysis("body", false, "body(1,15) : error : unexpected end", [])]),
        ]);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.Compilation);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("unexpected end");
        check.Location.ShouldBe("sms/pt/body");
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_used_but_undeclared_variable_fails_and_a_declared_but_unused_one_warns()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(("sms", "pt-BR", null, "Código {{ code }}", null));
        SetSchema(version, """{ "type": "object", "properties": { "minutes": { "type": "number" } } }""");

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("sms", "pt-BR", ("body", ["code"])),
        ]);

        ValidationCheck undeclared = report.Checks.Single(check => check.Name == ValidationCheckNames.VariablesDeclared);
        undeclared.Status.ShouldBe(ValidationCheckStatuses.Failed);
        undeclared.Message.ShouldContain("'code'");
        undeclared.Location.ShouldBe("sms/pt-BR/body");

        ValidationCheck unused = report.Checks.Single(check => check.Name == ValidationCheckNames.VariablesUsed);
        unused.Status.ShouldBe(ValidationCheckStatuses.Warning);
        unused.Message.ShouldContain("'minutes'");
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_literal_link_outside_the_allowlist_fails_and_a_subdomain_of_an_allowed_domain_passes()
    {
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = MakeVersion(
            ("email", "pt-BR", "Oi", "<a href=\"https://app.montebravo.com.br/pedidos\">ok</a>", "texto"),
            ("email", "en", "Hi", "<a href=\"https://evil.example.io/x\">bad</a>", "text"));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck failed = report.Checks.Single(check =>
            check.Name == ValidationCheckNames.UrlAllowlist && check.Status == ValidationCheckStatuses.Failed);
        failed.Message.ShouldContain("evil.example.io");
        failed.Location.ShouldBe("email/en/body");
    }

    [Fact]
    public void Any_link_fails_in_a_critical_template()
    {
        Template template = MakeTemplate(NotificationClass.Critical, linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = MakeVersion(("sms", "pt-BR", null, "Acesse https://montebravo.com.br/x", null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.UrlAllowlist);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("critical");
    }

    [Fact]
    public void A_link_fails_when_the_template_allows_no_domains()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(("sms", "pt-BR", null, "Acesse https://montebravo.com.br/x", null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.UrlAllowlist);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("no link domains");
    }

    [Fact]
    public void A_link_with_a_variable_host_fails()
    {
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = MakeVersion(("sms", "pt-BR", null, "Acesse https://{{ host }}/x", null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.UrlAllowlist);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("literal domain");
    }

    [Fact]
    public void A_url_variable_fails_when_the_template_allows_no_domains()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(("email", "pt-BR", "Oi", "{{ portalUrl }}", "texto"));
        SetSchema(version, """
            { "type": "object", "properties": { "portalUrl": { "type": "string", "format": "url" } } }
            """);

        ValidationReport report = TemplateValidation.Validate(template, version, [
            Analysis("email", "pt-BR", ("body", ["portalUrl"])),
        ]);

        ValidationCheck check = report.Checks.Single(candidate =>
            candidate.Name == ValidationCheckNames.UrlAllowlist && candidate.Status == ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'portalUrl'");
        check.Location.ShouldBe("email/pt-BR/body");
    }

    [Fact]
    public void A_sensitive_variable_in_a_url_position_fails()
    {
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"], sensitiveVariables: ["cpf"]);
        TemplateVersion version = MakeVersion(
            ("email", "pt-BR", "Oi", "Documento https://montebravo.com.br/consulta?doc={{ cpf }}", "texto"));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.SensitiveVariables);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'cpf'");
        check.Location.ShouldBe("email/pt-BR/body");
    }

    [Fact]
    public void A_sensitive_variable_outside_a_url_position_passes()
    {
        Template template = MakeTemplate(sensitiveVariables: ["cpf"]);
        TemplateVersion version = MakeVersion(("sms", "pt-BR", null, "Documento final {{ cpf }}", null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        report.Checks.Single(check => check.Name == ValidationCheckNames.SensitiveVariables)
            .Status.ShouldBe(ValidationCheckStatuses.Passed);
    }

    [Fact]
    public void An_sms_body_over_the_channel_limit_fails()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(
            ("sms", "pt-BR", null, new string('x', TemplateValidation.SmsMaxBodyChars + 1), null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.ChannelLimits);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Location.ShouldBe("sms/pt-BR/body");
    }

    [Fact]
    public void An_email_without_a_plain_text_version_fails()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(("email", "pt-BR", "Oi", "<p>corpo</p>", null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.ChannelLimits);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("plain-text");
        check.Location.ShouldBe("email/pt-BR/bodyText");
    }

    [Fact]
    public void A_push_title_over_the_channel_limit_fails()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(
            ("push", "pt-BR", new string('t', TemplateValidation.PushMaxSubjectChars + 1), "corpo", null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.ChannelLimits);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Location.ShouldBe("push/pt-BR/subject");
    }

    [Fact]
    public void A_template_without_a_default_locale_fails_completeness()
    {
        Template template = MakeTemplate(defaultLocale: null);
        TemplateVersion version = MakeVersion(("sms", "pt-BR", null, "corpo", null));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.DefaultLocale);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("no default locale");
    }

    [Fact]
    public void A_channel_without_content_for_the_default_locale_fails_completeness()
    {
        Template template = MakeTemplate();
        TemplateVersion version = MakeVersion(
            ("sms", "pt-BR", null, "corpo", null),
            ("email", "en", "Hi", "<p>body</p>", "body"));

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.DefaultLocale);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("'email'");
        check.Location.ShouldBe("email");
    }

    [Fact]
    public void A_version_without_content_fails_completeness()
    {
        Template template = MakeTemplate();
        TemplateVersion version = TemplateVersion.CreateDraft(Key, 1, "author-1", DateTimeOffset.UtcNow);

        ValidationReport report = TemplateValidation.Validate(template, version, []);

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == ValidationCheckNames.DefaultLocale);
        check.Status.ShouldBe(ValidationCheckStatuses.Failed);
        check.Message.ShouldContain("no content");
    }

    private static Template MakeTemplate(
        NotificationClass @class = NotificationClass.Transactional,
        string? defaultLocale = "pt-BR",
        IReadOnlyList<string>? linkDomains = null,
        IReadOnlyList<string>? sensitiveVariables = null)
        => Template.Create(Key, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = @class,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = defaultLocale is null ? null : Locale.Create(defaultLocale).Value,
            LinkDomainsAllowed = linkDomains ?? [],
            SensitiveVariables = sensitiveVariables ?? [],
        }).Value!;

    private static TemplateVersion MakeVersion(
        params (string Channel, string Locale, string? Subject, string Body, string? BodyText)[] contents)
    {
        var version = TemplateVersion.CreateDraft(Key, 1, "author-1", DateTimeOffset.UtcNow);
        foreach ((string channel, string locale, string? subject, string body, string? bodyText) in contents)
        {
            version.SetContent(
                new ContentEdit(
                    Channel.Create(channel).Value!,
                    Locale.Create(locale).Value!,
                    subject,
                    body,
                    bodyText),
                "author-1").IsSuccess.ShouldBeTrue();
        }

        return version;
    }

    private static void SetSchema(TemplateVersion version, string schemaJson)
        => version.SetVariablesSchema(schemaJson, "author-1").IsSuccess.ShouldBeTrue();

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
