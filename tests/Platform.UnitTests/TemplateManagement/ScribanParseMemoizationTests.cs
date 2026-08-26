using System.Globalization;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The eviction policy of the parse memoization, driven by the shape the
/// deployed renderer puts on it: one notification form is five sources, and a
/// published catalogue is a few dozen templates across channels and locales.
/// </summary>
public sealed class ScribanParseMemoizationTests
{
    /// <summary>Subject, body, text body and the two layout frames around them.</summary>
    private const int SourcesPerForm = 5;

    [Fact]
    public void A_catalog_of_a_thousand_sources_is_answered_from_memory_on_the_next_pass()
    {
        using var cache = new ScribanParseCache();
        var sources = FormSources(205);

        foreach (var source in sources)
        {
            cache.GetOrParse(source);
        }

        var parsedOnArrival = cache.Parses;
        var hitsOnArrival = cache.Hits;
        foreach (var source in sources)
        {
            cache.GetOrParse(source);
        }

        // Not one source of the hot set was parsed a second time. A policy that
        // counts entries instead of weighing them empties itself somewhere
        // inside the first pass, and this is where that shows up: the catalogue
        // that was already in memory has to be parsed again, form by form.
        (cache.Parses - parsedOnArrival).ShouldBe(
            0, "o conjunto quente inteiro cabe no orçamento e nada nele deveria ser reparseado");
        (cache.Hits - hitsOnArrival).ShouldBe(sources.Length);
        parsedOnArrival.ShouldBe(sources.Length);

        // What makes the second pass free is weight: the whole catalogue costs a
        // fraction of the budget, so nothing in it is ever a candidate.
        cache.ResidentChars.ShouldBeLessThan(ScribanParseCache.MaxSourceChars);
        sources.Distinct(StringComparer.Ordinal).Count().ShouldBe(sources.Length);
    }

    [Fact]
    public void A_source_at_the_size_limit_is_parsed_once_and_answered_from_memory_after_that()
    {
        using var cache = new ScribanParseCache();
        var limit = new TemplatingOptions().MaxTemplateSizeChars;
        var source = SourceOf(limit);

        for (var call = 0; call < 5; call++)
        {
            cache.GetOrParse(source);
        }

        // A source heavier than the whole budget is refused on arrival without a
        // word and parsed again on every single call. The margin between the
        // budget and the largest source a template may carry is what keeps that
        // from happening, so the largest one is what this drives.
        cache.Parses.ShouldBe(1);
        cache.Hits.ShouldBe(4);
        source.Length.ShouldBe(limit);
        cache.ResidentChars.ShouldBe(limit);
    }

    [Fact]
    public void A_catalog_far_past_the_budget_keeps_a_working_set_and_never_more_than_the_budget()
    {
        using var cache = new ScribanParseCache();
        var sources = FormSources(4_000);

        foreach (var source in sources)
        {
            cache.GetOrParse(source);
        }

        // Compaction is scheduled and not run inline, so the resident set keeps
        // shrinking for a moment after the write that filled the budget
        // returned: the claim is a bound it converges to, never a count read on
        // the spot.
        var settled = SpinWait.SpinUntil(
            () => cache.ResidentChars <= ScribanParseCache.MaxSourceChars, TimeSpan.FromSeconds(30));
        settled.ShouldBeTrue("a compactação agendada não devolveu orçamento dentro da espera");

        var resident = cache.ResidentChars;
        var offered = sources.Sum(source => (long)source.Length);

        // Bounded, and bounded by weight rather than by count.
        resident.ShouldBeLessThanOrEqualTo(ScribanParseCache.MaxSourceChars);

        // And still a working set. A policy that answers the pressure by
        // emptying itself is bounded too, and it is the failure this whole shape
        // exists to prevent.
        resident.ShouldBeGreaterThan(ScribanParseCache.MaxSourceChars / 2);

        // The offered catalogue outweighs the budget, so the policy had to
        // choose: without that the run would prove nothing about choosing.
        offered.ShouldBeGreaterThan((long)ScribanParseCache.MaxSourceChars);
    }

    /// <summary>
    /// The five sources of each form, distinct per form the way a catalogue is
    /// distinct: same shape, different content.
    /// </summary>
    private static string[] FormSources(int forms)
    {
        var sources = new string[forms * SourcesPerForm];
        for (var form = 0; form < forms; form++)
        {
            var mark = form.ToString(CultureInfo.InvariantCulture);
            var slot = form * SourcesPerForm;
            sources[slot] = "Pedido {{ order.id }} atualizado, aviso " + mark;
            sources[slot + 1] =
                "<p>Olá {{ user.name }}, o pedido {{ order.id }} tem {{ order.items.size }} itens "
                + "no aviso " + mark + ".</p>"
                + "<ul>{{ for item in order.items }}<li>{{ item.label }}: {{ item.qty }}</li>{{ end }}</ul>";
            sources[slot + 2] =
                "Olá {{ user.name }}, o pedido {{ order.id }} foi atualizado no aviso " + mark + ".";
            sources[slot + 3] =
                "<html><header>MB " + mark + "</header>{{ content }}<footer>rodapé</footer></html>";
            sources[slot + 4] = "MB " + mark + "\n{{ content }}\nrodapé";
        }

        return sources;
    }

    /// <summary>One source of exactly the given length, and a valid one.</summary>
    private static string SourceOf(int chars)
    {
        const string Head = "Olá {{ user.name }}, ";
        return Head + new string('.', chars - Head.Length);
    }
}
