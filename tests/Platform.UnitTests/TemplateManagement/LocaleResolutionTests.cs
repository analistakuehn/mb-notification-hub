using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class LocaleResolutionTests
{
    private static Locale L(string value) => Locale.Create(value).Value!;

    [Fact]
    public void An_exact_locale_match_wins_over_every_fallback()
    {
        Locale? resolved = LocaleResolution.Resolve(
            L("pt-BR"),
            [L("pt"), L("pt-BR"), L("en")],
            L("en"));

        resolved!.Value.ShouldBe("pt-BR");
    }

    [Fact]
    public void A_regional_locale_falls_back_to_its_base_language()
    {
        Locale? resolved = LocaleResolution.Resolve(
            L("pt-BR"),
            [L("pt"), L("en")],
            L("en"));

        resolved!.Value.ShouldBe("pt");
    }

    [Fact]
    public void An_unmatched_locale_falls_back_to_the_template_default()
    {
        Locale? resolved = LocaleResolution.Resolve(
            L("es-AR"),
            [L("pt-BR"), L("en")],
            L("pt-BR"));

        resolved!.Value.ShouldBe("pt-BR");
    }

    [Fact]
    public void A_base_language_request_does_not_match_a_regional_variant()
    {
        Locale? resolved = LocaleResolution.Resolve(
            L("pt"),
            [L("pt-BR"), L("en")],
            L("en"));

        resolved!.Value.ShouldBe("en");
    }

    [Fact]
    public void Resolution_fails_when_nothing_matches_and_the_template_has_no_default()
    {
        Locale? resolved = LocaleResolution.Resolve(
            L("es-AR"),
            [L("pt-BR"), L("en")],
            defaultLocale: null);

        resolved.ShouldBeNull();
    }

    [Fact]
    public void Resolution_fails_when_the_default_locale_has_no_content()
    {
        Locale? resolved = LocaleResolution.Resolve(
            L("es-AR"),
            [L("en")],
            L("pt-BR"));

        resolved.ShouldBeNull();
    }
}
