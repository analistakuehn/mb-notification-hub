using System.Text.Json;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Where an author finds out that a template picks its own culture. The render
/// refuses it, so a version carrying one cannot produce a message at all; what
/// these cases pin is that the refusal arrives while the author is publishing
/// and not while a notification is going out.
/// </summary>
public sealed class OutputCultureValidationTests
{
    private const string WithCulture = """Total {{ valor | math.format "N2" "pt-BR" }}""";
    private const string WithoutCulture = """Total {{ valor | math.format "N2" }}""";

    private static readonly TemplateKey TemplateName = TemplateKey.Create("pedido.confirmado").Value!;
    private static readonly LayoutKey LayoutName = LayoutKey.Create("email.base").Value!;
    private static readonly DateTimeOffset CreatedAt = new(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly Channel Email = Channel.Create("email").Value!;
    private static readonly Locale PtBr = Locale.Create("pt-BR").Value!;

    /// <summary>Every spelling the publication-time check can resolve on its own.</summary>
    private static readonly string[] VisibleSources =
    [
        """{{ 1234567.5 | math.format "N1" "pt-BR" }}""",
        """{{ math.format 1234567.5 "N1" "pt-BR" }}""",
        """{{ math.format(1234567.5, "N1", "pt-BR") }}""",
        """{{ 1234567.5 | math.format format: "N1" culture: "pt-BR" }}""",
        """{{ cultura = "pt-BR"; 1234567.5 | math.format "N1" cultura }}""",
        """{{ 1234567.5 | math["format"] "N1" "pt-BR" }}""",
    ];

    public static TheoryData<string> Visible => new(VisibleSources);

    private static ScribanTemplateEngine Engine()
        => new(Options.Create(new TemplatingOptions()), new ScribanParseCache());

    [Theory]
    [MemberData(nameof(Visible))]
    public void The_analysis_names_the_member_a_source_hands_a_culture_to(string source)
        => Engine().Analyze(source, "body").CultureArguments.ShouldBe(["math.format"]);

    [Fact]
    public void A_source_that_names_no_culture_reports_nothing()
        => Engine().Analyze(WithoutCulture, "body").CultureArguments.ShouldBeEmpty();

    [Fact]
    public void The_same_member_reached_twice_is_reported_once()
        => Engine()
            .Analyze(
                """{{ 1 | math.format "N1" "pt-BR" }}{{ 2 | math.format "N1" "en-US" }}""",
                "body")
            .CultureArguments
            .ShouldBe(["math.format"]);

    [Fact]
    public async Task A_group_reached_through_a_variable_escapes_the_publication_check_and_not_the_render()
    {
        // The blind spot, asserted instead of described. The publication check
        // reads the syntax, and the syntax here says the call is on a variable;
        // the ban reads the call the engine actually made. Whoever widens the
        // first one should watch this case flip, and whoever narrows the second
        // one should watch a template go out with a culture in it.
        const string Aliased = """{{ grupo = math; 1234567.5 | grupo.format "N1" "pt-BR" }}""";

        Engine().Analyze(Aliased, "body").CultureArguments.ShouldBeEmpty();

        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            Aliased, variables: null, CancellationToken.None);

        outcome.Refusal.ShouldBe(TemplateRefusal.CultureArgument);
    }

    [Fact]
    public void A_template_version_that_passes_a_culture_fails_the_check_naming_the_field()
    {
        TemplateVersion version = TemplateDraft(WithCulture);
        var analyzer = new TemplateVersionAnalyzer(Engine());

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(), version, analyzer.Analyze(version));

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == "output-culture");
        check.Status.ShouldBe("failed");
        check.Location.ShouldBe("email/pt-BR/body");
        check.Message.ShouldContain("math.format");
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_template_version_that_names_no_culture_passes_the_check()
    {
        TemplateVersion version = TemplateDraft(WithoutCulture);
        var analyzer = new TemplateVersionAnalyzer(Engine());

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(), version, analyzer.Analyze(version));

        report.Checks.Single(candidate => candidate.Name == "output-culture").Status.ShouldBe("passed");
    }

    [Fact]
    public void A_layout_version_that_passes_a_culture_fails_the_check_naming_the_field()
    {
        // The second path, and the reason it has a case of its own: a layout
        // renders on the same engine and is refused by the same ban, so a
        // layout left out here would publish clean and break every template
        // that pins it.
        LayoutVersion version = LayoutDraft("<html>" + WithCulture + "{{ content }}</html>");
        var analyzer = new LayoutVersionAnalyzer(Engine());

        ValidationReport report = LayoutValidation.Validate(version, analyzer.Analyze(version));

        ValidationCheck check = report.Checks.Single(candidate => candidate.Name == "output-culture");
        check.Status.ShouldBe("failed");
        check.Location.ShouldBe("email/pt-BR/body");
        check.Message.ShouldContain("math.format");
        report.Passed.ShouldBeFalse();
    }

    [Fact]
    public void A_layout_version_that_names_no_culture_passes_the_check()
    {
        LayoutVersion version = LayoutDraft("<html>" + WithoutCulture + "{{ content }}</html>");
        var analyzer = new LayoutVersionAnalyzer(Engine());

        ValidationReport report = LayoutValidation.Validate(version, analyzer.Analyze(version));

        report.Checks.Single(candidate => candidate.Name == "output-culture").Status.ShouldBe("passed");
    }

    [Fact]
    public async Task The_publication_check_and_the_render_say_the_same_sentence()
    {
        // One mistake, one wording. Two spellings of it would drift, and which
        // one an author sees would depend on which door they arrived through.
        TemplateVersion version = TemplateDraft(WithCulture);
        var analyzer = new TemplateVersionAnalyzer(Engine());

        ValidationReport report = TemplateValidation.Validate(
            NewTemplate(), version, analyzer.Analyze(version));

        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            WithCulture, Variables("""{"valor":1234567.5}"""), CancellationToken.None);

        outcome.Refusal.ShouldBe(TemplateRefusal.CultureArgument);
        outcome.Result.Error.ShouldBe(
            report.Checks.Single(candidate => candidate.Name == "output-culture").Message);
    }

    [Fact]
    public async Task A_culture_the_render_never_reaches_is_still_named_at_publication()
    {
        // The render is a runtime guard and answers about the expression it
        // executed: with no payload, the undeclared variable is reported first
        // and the culture is never reached, so the author is told about the
        // variable and nothing else. The publication check reads the source and
        // reports both. This is the gap the check exists to cover, and a
        // template whose payload happens to be complete on the day it is
        // previewed would otherwise be the only one that gets warned.
        TemplateRenderOutcome outcome = await Engine().RenderOutcomeAsync(
            WithCulture, variables: null, CancellationToken.None);

        outcome.Refusal.ShouldBe(TemplateRefusal.Unclassified);
        outcome.Result.Error.ShouldNotBeNull();
        outcome.Result.Error.ShouldNotContain("math.format");

        Engine().Analyze(WithCulture, "body").CultureArguments.ShouldBe(["math.format"]);
    }

    private static JsonElement Variables(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static Template NewTemplate()
        => Template.Create(TemplateName, Metadata()).Value!;

    private static TemplateMetadata Metadata()
        => new()
        {
            Application = "araia-cambio",
            Class = NotificationClass.Transactional,
            OwnerTeam = "growth-squad",
            Purpose = "pedido",
            LegalBasis = "execucao-de-contrato",
            DefaultLocale = PtBr,
            LinkDomainsAllowed = [],
        };

    private static TemplateVersion TemplateDraft(string body)
    {
        var version = TemplateVersion.CreateDraft(TemplateName, 1, "author-1", CreatedAt);
        version.SetContent(new ContentEdit(Email, PtBr, "Assunto", body, null), "author-1")
            .IsSuccess.ShouldBeTrue();
        return version;
    }

    private static LayoutVersion LayoutDraft(string body)
    {
        var version = LayoutVersion.CreateDraft(LayoutName, 1, "author-1", CreatedAt);
        version.SetContent(new LayoutContentEdit(Email, PtBr, body, null), "author-1")
            .IsSuccess.ShouldBeTrue();
        return version;
    }
}
