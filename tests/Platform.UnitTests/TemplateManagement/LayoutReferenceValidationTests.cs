using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LayoutReferenceValidationTests
{
    private static readonly TemplateKey Key = TemplateKey.Create("orders.updated").Value!;
    private static readonly LayoutKey BaseLayout = LayoutKey.Create("email.base").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    [Fact]
    public void A_published_layout_covering_every_content_entry_passes_the_check()
    {
        TemplateVersion version = DraftWithLayout();

        ValidationReport report = TemplateValidation.Validate(NewTemplate(), version, [], Facts());

        report.Checks.ShouldContain(check =>
            check.Name == "layout-reference" && check.Status == "passed");
    }

    [Fact]
    public void A_missing_layout_fails_the_check()
    {
        TemplateVersion version = DraftWithLayout();

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(), version, [], Facts() with { LayoutExists = false });

        ValidationCheck failed = report.Checks.Single(check => check.Name == "layout-reference");
        failed.Status.ShouldBe("failed");
        failed.Message.ShouldContain("does not exist");
    }

    [Fact]
    public void A_missing_pinned_version_fails_the_check()
    {
        TemplateVersion version = DraftWithLayout();

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(), version, [], Facts() with { VersionExists = false, VersionStatus = null, Contents = [] });

        ValidationCheck failed = report.Checks.Single(check => check.Name == "layout-reference");
        failed.Status.ShouldBe("failed");
        failed.Message.ShouldContain("has no version 3");
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("superseded")]
    public void A_pinned_version_that_is_not_published_fails_the_check(string status)
    {
        TemplateVersion version = DraftWithLayout();

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(), version, [], Facts() with { VersionStatus = status });

        ValidationCheck failed = report.Checks.Single(check => check.Name == "layout-reference");
        failed.Status.ShouldBe("failed");
        failed.Message.ShouldContain($"'{status}', not published");
    }

    [Fact]
    public void A_layout_without_content_for_a_template_channel_and_locale_fails_naming_the_pair()
    {
        TemplateVersion version = DraftWithLayout();

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(), version, [], Facts() with { Contents = [new ContentUnit("sms", "pt-BR")] });

        ValidationCheck failed = report.Checks.Single(check => check.Name == "layout-reference");
        failed.Status.ShouldBe("failed");
        failed.Location.ShouldBe("email/pt-BR");
    }

    [Fact]
    public void Layout_content_resolves_through_base_language_and_default_locale()
    {
        TemplateVersion version = DraftWithLayout();

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(),
            version,
            [],
            Facts() with { Contents = [new ContentUnit("email", "pt")] });

        report.Checks.Single(check => check.Name == "layout-reference").Status.ShouldBe("passed");
    }

    [Fact]
    public void A_version_without_a_layout_reference_produces_no_layout_check_and_stays_valid()
    {
        var version = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        version.SetContent(new ContentEdit(Email, PtBr, "Assunto", "corpo", "texto"), "author-1");

        ValidationReport report = TemplateValidation.Validate(NewTemplate(), version, []);

        report.Checks.ShouldNotContain(check => check.Name == "layout-reference");
        report.Passed.ShouldBeTrue();
    }

    private static Template NewTemplate()
        => Template.Create(Key, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "order-updates",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = PtBr,
        }).Value!;

    private static TemplateVersion DraftWithLayout()
    {
        var version = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        version.SetContent(new ContentEdit(Email, PtBr, "Assunto", "corpo", "texto"), "author-1");
        version.SetLayoutReference(BaseLayout, 3, "author-1");
        return version;
    }

    private static LayoutReferenceFacts Facts() => new()
    {
        LayoutKey = "email.base",
        LayoutVersion = 3,
        LayoutExists = true,
        VersionExists = true,
        VersionStatus = "published",
        DefaultLocale = "pt",
        Contents = [new ContentUnit("email", "pt-BR")],
    };
}
