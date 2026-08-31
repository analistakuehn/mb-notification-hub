using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// A layout is authored once and pinned by many templates, and its text reaches
/// the reader inside the message of every one of them. The publication catalog
/// therefore has to rule on the wrapper as well: the allowed domains of the
/// pinning template, the link ban of an authentication SMS, and the channel
/// ceiling all apply to what the layout puts around the content.
/// </summary>
public sealed class LayoutContentLinkValidationTests
{
    private const string ForeignLink = """<a href="https://evil.example.io/x">ir</a>{{ content }}""";

    private static readonly TemplateKey Key = TemplateKey.Create("orders.status.changed").Value!;
    private static readonly LayoutKey BaseLayout = LayoutKey.Create("email.base").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 22, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Channel Sms = Channel.Create("sms").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    [Fact]
    public void A_pinned_layout_carrying_a_host_outside_the_allowed_domains_fails_the_url_allowlist_check()
    {
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);

        ValidationReport report = TemplateValidation.Validate(
            template, version, [], Facts(new LayoutContentFacts("email", "pt-BR", ForeignLink, null)));

        report.Passed.ShouldBeFalse();
        ValidationCheck failed = report.Checks.Single(check => check.Name == "url-allowlist");
        failed.Status.ShouldBe("failed");
        failed.Message.ShouldContain("evil.example.io");
    }

    [Fact]
    public void A_pinned_layout_link_names_the_layout_and_the_field_in_the_check_location()
    {
        // The text is not the template author's, so a location pointing at
        // their own body would send them looking for a link that is not there.
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);

        ValidationReport report = TemplateValidation.Validate(
            template,
            version,
            [],
            Facts(new LayoutContentFacts("email", "pt-BR", ForeignLink, "evil.example.io/x {{ content }}")));

        report.Checks
            .Where(check => check.Name == "url-allowlist")
            .Select(check => check.Location)
            .ShouldBe([
                "layout:email.base@3/email/pt-BR/body",
                "layout:email.base@3/email/pt-BR/bodyText",
            ]);
    }

    [Fact]
    public void A_pinned_layout_link_blocks_an_authentication_sms_template_at_publication()
    {
        // Without this the render refuses every time, and the refusal only
        // shows up in production as an authentication code that never arrives.
        Template template = MakeTemplate(purpose: TemplateValidation.AuthenticationPurpose);
        TemplateVersion version = DraftPinningLayout(Sms, "Código 123456. Não compartilhe.", bodyText: null);

        ValidationReport report = TemplateValidation.Validate(
            template, version, [], Facts(new LayoutContentFacts("sms", "pt-BR", "MB: {{ content }} bit.ly/x9k2p", null)));

        report.Passed.ShouldBeFalse();
        ValidationCheck failed = report.Checks.Single(check => check.Name == "authentication-sms-links");
        failed.Status.ShouldBe("failed");
        failed.Location.ShouldBe("layout:email.base@3/sms/pt-BR/body");
    }

    [Fact]
    public void A_pinned_layout_link_leaves_a_template_of_another_purpose_alone()
    {
        // Falsification: the purpose triggers the ban, not the layout text.
        Template template = MakeTemplate(linkDomains: ["bit.ly"]);
        TemplateVersion version = DraftPinningLayout(Sms, "Pedido atualizado.", bodyText: null);

        ValidationReport report = TemplateValidation.Validate(
            template, version, [], Facts(new LayoutContentFacts("sms", "pt-BR", "MB: {{ content }} bit.ly/x9k2p", null)));

        report.Checks.ShouldNotContain(check => check.Name == "authentication-sms-links");
        report.Passed.ShouldBeTrue();
    }

    [Fact]
    public void A_pinned_layout_inside_the_allowed_domains_keeps_the_url_allowlist_check_passed()
    {
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);
        var framing = """<a href="https://montebravo.com.br/x">MB</a>{{ content }}""";

        ValidationReport report = TemplateValidation.Validate(
            template, version, [], Facts(new LayoutContentFacts("email", "pt-BR", framing, null)));

        report.Checks.Single(check => check.Name == "url-allowlist").Status.ShouldBe("passed");
        report.Passed.ShouldBeTrue();
    }

    [Fact]
    public void An_unpublished_pinned_version_reports_only_the_layout_reference_failure()
    {
        // A broken pin has one cause. Every rule that reads the layout text
        // stays quiet so the report keeps pointing at it.
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);

        ValidationReport report = TemplateValidation.Validate(
            template,
            version,
            [],
            Facts(new LayoutContentFacts("email", "pt-BR", ForeignLink, null)) with { VersionStatus = "draft" });

        report.Checks
            .Where(check => check.Status == "failed")
            .Select(check => check.Name)
            .ShouldBe(["layout-reference"]);
    }

    [Fact]
    public void A_missing_layout_reports_only_the_layout_reference_failure()
    {
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);

        ValidationReport report = TemplateValidation.Validate(
            template,
            version,
            [],
            Facts(new LayoutContentFacts("email", "pt-BR", ForeignLink, null)) with { LayoutExists = false });

        report.Checks
            .Where(check => check.Status == "failed")
            .Select(check => check.Name)
            .ShouldBe(["layout-reference"]);
    }

    [Fact]
    public void The_layout_content_that_answers_is_the_one_the_locale_chain_resolves()
    {
        // pt-BR content has no exact wrapper, so the base language answers,
        // and the check has to rule on that text, not on the default locale's.
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);

        ValidationReport report = TemplateValidation.Validate(
            template,
            version,
            [],
            Facts(
                new LayoutContentFacts("email", "pt", """<a href="https://pt.example.io/x">ir</a>{{ content }}""", null),
                new LayoutContentFacts("email", "en", """<a href="https://en.example.io/x">go</a>{{ content }}""", null))
                with { DefaultLocale = "en" });

        ValidationCheck failed = report.Checks.Single(check => check.Name == "url-allowlist");
        failed.Message.ShouldContain("pt.example.io");
        failed.Location.ShouldBe("layout:email.base@3/email/pt-BR/body");
    }

    [Fact]
    public void A_layout_body_plus_a_template_body_over_the_sms_ceiling_fails_the_channel_limits_check()
    {
        // Neither half is over the ceiling; what leaves the platform is the sum.
        Template template = MakeTemplate();
        TemplateVersion version = DraftPinningLayout(Sms, new string('a', 900), bodyText: null);
        var framing = "{{ content }}" + new string('b', 800);

        ValidationReport report = TemplateValidation.Validate(
            template, version, [], Facts(new LayoutContentFacts("sms", "pt-BR", framing, null)));

        report.Passed.ShouldBeFalse();
        ValidationCheck failed = report.Checks.Single(check => check.Name == "channel-limits");
        failed.Status.ShouldBe("failed");
        failed.Location.ShouldBe("sms/pt-BR/body");
    }

    [Fact]
    public void A_pinned_layout_with_an_allowed_host_publishes_under_a_critical_template()
    {
        // The layout answers to the allowlist of the template that pins it, and
        // escapes the class-wide ban: shared framing carries a logo from the
        // CDN, and banning it would make every layout unusable by a critical
        // template, with no allowlist entry able to undo the refusal.
        Template template = MakeTemplate(NotificationClass.Critical, linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);
        var framing = """<img src="https://cdn.montebravo.com.br/logo.png">{{ content }}""";

        ValidationReport report = TemplateValidation.Validate(
            template, version, [], Facts(new LayoutContentFacts("email", "pt-BR", framing, null)));

        report.Checks.Single(check => check.Name == "url-allowlist").Status.ShouldBe("passed");
        report.Passed.ShouldBeTrue();
    }

    [Fact]
    public void An_address_written_inside_a_doctype_still_yields_its_host_in_plain_text()
    {
        // The two fields go through different doors. Stripping a DOCTYPE is
        // right for markup, where the declaration is not something a reader can
        // act on, and wrong for the text variant, where a mail client that
        // auto-links turns the very same characters into a link to tap.
        Template template = MakeTemplate(linkDomains: ["montebravo.com.br"]);
        TemplateVersion version = DraftPinningLayout(Email);
        const string Hidden = "<!doctype x https://evil.example.io/y>";

        ValidationReport report = TemplateValidation.Validate(
            template,
            version,
            [],
            Facts(new LayoutContentFacts("email", "pt-BR", Hidden + "{{ content }}", Hidden + " {{ content }}")));

        report.Checks
            .Where(check => check.Name == "url-allowlist")
            .Select(check => check.Location)
            .ShouldBe(["layout:email.base@3/email/pt-BR/bodyText"]);
    }

    private static Template MakeTemplate(
        NotificationClass notificationClass = NotificationClass.Transactional,
        string purpose = "order-updates",
        IReadOnlyList<string>? linkDomains = null)
        => Template.Create(Key, new TemplateMetadata
        {
            Application = "araia-cambio",
            Class = notificationClass,
            OwnerTeam = "growth-squad",
            Purpose = purpose,
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = PtBr,
            LinkDomainsAllowed = linkDomains ?? [],
        }).Value!;

    private static TemplateVersion DraftPinningLayout(
        Channel channel,
        string body = "corpo",
        string? bodyText = "texto")
    {
        var version = TemplateVersion.CreateDraft(Key, 1, "author-1", CreatedAt);
        version.SetContent(
            new ContentEdit(channel, PtBr, channel == Email ? "Assunto" : null, body, bodyText),
            "author-1").IsSuccess.ShouldBeTrue();
        version.SetLayoutReference(BaseLayout, 3, "author-1");
        return version;
    }

    private static LayoutReferenceFacts Facts(params LayoutContentFacts[] contents) => new()
    {
        LayoutKey = "email.base",
        LayoutVersion = 3,
        LayoutExists = true,
        VersionExists = true,
        VersionStatus = "published",
        DefaultLocale = "pt-BR",
        Contents = contents,
    };
}
