using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LayoutValidationTests
{
    private static readonly LayoutKey Key = LayoutKey.Create("email.base").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;
    private static readonly Channel Sms = Channel.Create("sms").Value!;

    [Fact]
    public void A_wrapper_reading_the_content_variable_passes_every_check()
    {
        LayoutVersion version = DraftWith("<html>{{ content }}</html>");

        ValidationReport report = LayoutValidation.Validate(version, [Analysis(usedVariables: ["content"])]);

        report.Passed.ShouldBeTrue();
        report.Checks.ShouldContain(check =>
            check.Name == "content-placeholder" && check.Status == "passed");
    }

    [Fact]
    public void A_wrapper_that_never_reads_content_fails_the_placeholder_check_naming_the_field()
    {
        LayoutVersion version = DraftWith("<html>estático</html>");

        ValidationReport report = LayoutValidation.Validate(version, [Analysis(usedVariables: [])]);

        report.Passed.ShouldBeFalse();
        ValidationCheck failed = report.Checks.Single(check =>
            check.Name == "content-placeholder" && check.Status == "failed");
        failed.Location.ShouldBe("email/pt-BR/body");
        failed.Message.ShouldContain("'content'");
    }

    [Fact]
    public void A_parse_failure_fails_compilation_and_skips_the_placeholder_verdict_for_that_field()
    {
        LayoutVersion version = DraftWith("{{ content ");

        ValidationReport report = LayoutValidation.Validate(
            version,
            [Analysis(parseSucceeded: false, parseError: "unexpected end of template")]);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "compilation" && check.Status == "failed");
        report.Checks.ShouldNotContain(check =>
            check.Name == "content-placeholder" && check.Status == "failed");
    }

    [Fact]
    public void An_empty_version_fails_the_placeholder_check()
    {
        var version = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);

        ValidationReport report = LayoutValidation.Validate(version, []);

        report.Passed.ShouldBeFalse();
        report.Checks.ShouldContain(check =>
            check.Name == "content-placeholder"
            && check.Status == "failed"
            && check.Message.Contains("no content"));
    }

    [Fact]
    public void An_sms_wrapper_longer_than_the_sms_ceiling_fails_the_channel_limits_check()
    {
        var body = "{{ content }}" + new string('a', TemplateValidation.SmsMaxBodyChars);
        LayoutVersion version = DraftWith(body, Sms);

        ValidationReport report = LayoutValidation.Validate(
            version, [Analysis(usedVariables: ["content"], channel: Sms)]);

        report.Passed.ShouldBeFalse();
        ValidationCheck failed = report.Checks.Single(check =>
            check.Name == "channel-limits" && check.Status == "failed");
        failed.Location.ShouldBe("sms/pt-BR/body");
    }

    [Fact]
    public void An_email_wrapper_without_a_text_variant_keeps_the_channel_limits_check_passed()
    {
        // The plain-text wrapper of a layout is optional by contract: the
        // render only wraps the text when the template carries one as well.
        LayoutVersion version = DraftWith("<html>{{ content }}</html>");

        ValidationReport report = LayoutValidation.Validate(version, [Analysis(usedVariables: ["content"])]);

        report.Checks.Single(check => check.Name == "channel-limits").Status.ShouldBe("passed");
    }

    [Fact]
    public void A_layout_carrying_a_link_host_warns_without_failing()
    {
        // Without a template there is no allowlist to decide against, so the
        // catalog can only tell the author what every template that pins this
        // layout will have to allow. Refusing here would be deciding with no
        // operand.
        LayoutVersion version = DraftWith("""<a href="https://cdn.montebravo.com.br/x">MB</a>{{ content }}""");

        ValidationReport report = LayoutValidation.Validate(version, [Analysis(usedVariables: ["content"])]);

        report.Passed.ShouldBeTrue();
        ValidationCheck warning = report.Checks.Single(check => check.Name == "url-allowlist");
        warning.Status.ShouldBe("warning");
        warning.Message.ShouldContain("cdn.montebravo.com.br");
        warning.Location.ShouldBe("email/pt-BR/body");
    }

    [Fact]
    public void An_institutional_footer_with_a_document_number_raises_no_link_warning()
    {
        // Pins the narrow detector. The wide one, the one that bans anything
        // clickable from an authentication SMS, reads a document number
        // followed by a slash as a link, and a warning that cries wolf is a
        // warning the author learns to scroll past.
        LayoutVersion version = DraftWith("<footer>CNPJ 12.345.678/0001-90</footer>{{ content }}");

        ValidationReport report = LayoutValidation.Validate(version, [Analysis(usedVariables: ["content"])]);

        report.Checks.Single(check => check.Name == "url-allowlist").Status.ShouldBe("passed");
    }

    private static LayoutVersion DraftWith(string body, Channel? channel = null)
    {
        var version = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        version.SetContent(new LayoutContentEdit(channel ?? Email, PtBr, body, null), "author-1")
            .IsSuccess.ShouldBeTrue();
        return version;
    }

    private static ContentAnalysis Analysis(
        bool parseSucceeded = true,
        string? parseError = null,
        IReadOnlyList<string>? usedVariables = null,
        Channel? channel = null)
        => new(
            channel ?? Email,
            PtBr,
            [new ContentFieldAnalysis("body", parseSucceeded, parseError, usedVariables ?? [])]);
}
