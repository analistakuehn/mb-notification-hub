using System.Text;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The normalizer decides which bytes a carrier receives, and the audited hash
/// describes exactly those bytes, so every rule here is also a rule about what
/// the trail will claim was sent. Every literal is written with escapes on
/// purpose: the characters under test are invisible, and a source file is not
/// a trustworthy place to keep them.
/// </summary>
public sealed class SmsContentNormalizerTests
{
    /// <summary>The accented word as a keyboard produces it, one code point per letter.</summary>
    private const string Composed = "Código de acesso válido";

    /// <summary>The same word with the accents as combining marks.</summary>
    private const string Decomposed = "Código de acesso válido";

    [Fact]
    public void A_decomposed_accent_becomes_the_composed_form()
    {
        // Premise first: an input that is already composed would make the
        // assertion below pass without the normalizer doing anything.
        Decomposed.IsNormalized(NormalizationForm.FormC).ShouldBeFalse();

        var normalized = SmsContentNormalizer.Normalize(Decomposed);

        normalized.ShouldBe(Composed);
        normalized.Length.ShouldBeLessThan(Decomposed.Length);
    }

    [Fact]
    public void Text_already_composed_survives_untouched()
    {
        // Falsification of the test above: normalization is the identity on
        // text already in the shipped form, or it would rewrite every message
        // and no recorded hash would ever match a fresh render.
        SmsContentNormalizer.Normalize(Composed).ShouldBe(Composed);
    }

    [Fact]
    public void Line_breaks_leave_one_space_and_never_weld_two_words()
    {
        var normalized = SmsContentNormalizer.Normalize("primeira linha\r\nsegunda linha");

        normalized.ShouldBe("primeira linha segunda linha");
        normalized.ShouldNotContain("\n");
        normalized.ShouldNotContain("\r");
    }

    [Fact]
    public void Control_and_format_characters_are_removed()
    {
        // A right-to-left override inside an authentication message reorders
        // what the person reads without changing what was written, and a zero
        // width space hides a word boundary. Neither one is content.
        var withOverride = "Có​digo‮ de acesso: 123456";

        SmsContentNormalizer.Normalize(withOverride).ShouldBe("Código de acesso: 123456");
    }

    [Fact]
    public void Repeated_whitespace_collapses_and_the_edges_are_trimmed()
        => SmsContentNormalizer.Normalize("  código   de \t acesso  ")
            .ShouldBe("código de acesso");

    [Fact]
    public void An_unpaired_surrogate_leaves_the_stripped_text_instead_of_throwing()
    {
        // Composition is impossible over a broken code unit, and refusing the
        // render there would trade a deliverable message for none.
        var normalized = SmsContentNormalizer.Normalize("código \ud800 de acesso");

        normalized.ShouldContain("digo");
        normalized.ShouldContain("de acesso");
    }

    [Fact]
    public void An_empty_body_stays_empty()
        => SmsContentNormalizer.Normalize("").ShouldBeEmpty();
}
