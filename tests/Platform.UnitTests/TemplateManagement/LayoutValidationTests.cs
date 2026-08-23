using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LayoutValidationTests
{
    private static readonly LayoutKey Key = LayoutKey.Create("email.base").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

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

    private static LayoutVersion DraftWith(string body)
    {
        var version = LayoutVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        version.SetContent(new LayoutContentEdit(Email, PtBr, body, null), "author-1").IsSuccess.ShouldBeTrue();
        return version;
    }

    private static ContentAnalysis Analysis(
        bool parseSucceeded = true,
        string? parseError = null,
        IReadOnlyList<string>? usedVariables = null)
        => new(
            Email,
            PtBr,
            [new ContentFieldAnalysis("body", parseSucceeded, parseError, usedVariables ?? [])]);
}
