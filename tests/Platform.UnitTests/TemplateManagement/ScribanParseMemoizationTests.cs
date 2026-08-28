using System.Globalization;
using System.Text;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using Scriban;

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

    /// <summary>
    /// One filler of the budget. Small enough that what is left over when the
    /// budget is loaded is a rounding error, and heavy enough that loading it
    /// costs a handful of parses instead of thousands.
    /// </summary>
    private const int FillerChars = 5_000;

    [Fact]
    public void A_catalog_of_a_thousand_sources_is_answered_from_memory_on_the_next_pass()
    {
        using var cache = new ScribanParseCache();
        var sources = FormSources(205);

        foreach (var source in sources)
        {
            Memoize(cache, source);
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
        cache.ResidentBytes.ShouldBeLessThan(ScribanParseCache.MaxResidentBytes);
        sources.Distinct(StringComparer.Ordinal).Count().ShouldBe(sources.Length);
    }

    [Fact]
    public void A_source_at_the_size_limit_is_answered_from_memory_on_an_empty_store_and_on_a_full_one()
    {
        var limit = new TemplatingOptions().MaxTemplateSizeChars;
        var source = DenseSource(limit, mark: 0);
        var weight = ScribanParseCache.WeightOf(source, Template.Parse(source));

        source.Length.ShouldBe(limit);

        // The heaviest source a template may carry has to fit the budget with
        // room to spare. One that alone outweighed it would be refused on
        // arrival without a word and parsed again on every single call, which
        // reads as a slow renderer and never as a ceiling set too high.
        weight.ShouldBeLessThan(
            ScribanParseCache.MaxResidentBytes,
            "o teto de tamanho de fonte precisa caber no orçamento no pior caso de densidade");

        using (var empty = new ScribanParseCache())
        {
            Memoize(empty, source);
            for (var call = 0; call < 4; call++)
            {
                empty.GetOrParse(source);
            }

            empty.Parses.ShouldBe(1);
            empty.Hits.ShouldBe(4);
            empty.ResidentBytes.ShouldBe(weight);
        }

        // And the same source against a budget that has no room for it, which
        // is the only state the admission gate is ever exercised in. An empty
        // store admits anything, so a claim proved only there says nothing
        // about a renderer that has been up for a while.
        using var loaded = new ScribanParseCache();
        Fill(loaded, weight);
        Memoize(loaded, source);

        var hitsBefore = loaded.Hits;
        loaded.GetOrParse(source);
        (loaded.Hits - hitsBefore).ShouldBe(
            1, "a maior fonte que o motor aceita precisa entrar mesmo com o orçamento carregado");
    }

    [Fact]
    public void A_catalog_far_past_the_budget_keeps_a_working_set_and_never_more_than_the_budget()
    {
        using var cache = new ScribanParseCache();
        var offered = 0L;
        var forms = 0;

        // Offered until the catalogue outweighs the budget by a quarter, so the
        // policy had to choose: without that the run would prove nothing about
        // choosing.
        var target = ScribanParseCache.MaxResidentBytes + (ScribanParseCache.MaxResidentBytes / 4);
        while (offered <= target)
        {
            foreach (var source in Form(forms))
            {
                offered += Memoize(cache, source);
            }

            forms++;
        }

        // The store compacts inline, before it takes an entry in, so the budget
        // is never crossed rather than crossed and walked back. The wait stays
        // because the store also schedules a pass of its own after a refusal it
        // resolved by itself, and that one lands whenever the pool runs it.
        var settled = SpinWait.SpinUntil(
            () => cache.ResidentBytes <= ScribanParseCache.MaxResidentBytes, TimeSpan.FromSeconds(30));
        settled.ShouldBeTrue("a compactação não devolveu orçamento dentro da espera");

        var resident = cache.ResidentBytes;
        offered.ShouldBeGreaterThan(ScribanParseCache.MaxResidentBytes);

        // Bounded, and bounded by weight rather than by count.
        resident.ShouldBeLessThanOrEqualTo(ScribanParseCache.MaxResidentBytes);

        // And still a working set. A policy that answers the pressure by
        // emptying itself is bounded too, and it is the failure this whole shape
        // exists to prevent.
        resident.ShouldBeGreaterThan(ScribanParseCache.MaxResidentBytes / 2);
        (forms * SourcesPerForm).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void A_source_larger_than_the_compaction_slice_is_still_answered_from_memory_when_the_budget_is_full()
    {
        using var cache = new ScribanParseCache();
        var slice = (long)(ScribanParseCache.MaxResidentBytes * ScribanParseCache.CompactionShare);
        var large = DenseSource(25_000, mark: 0);
        var weight = ScribanParseCache.WeightOf(large, Template.Parse(large));

        // The budget full and the arriving source heavier than what one
        // compaction pass frees. Both are preconditions of the claim, and
        // without them the run would prove nothing about the policy.
        weight.ShouldBeGreaterThan(slice, "a fonte precisa pesar mais do que uma passagem de compactação libera");
        Fill(cache, weight);
        cache.ResidentBytes.ShouldBeGreaterThan(
            ScribanParseCache.MaxResidentBytes - weight, "o orçamento precisa estar cheio");

        var answered = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var hitsBefore = cache.Hits;
            Memoize(cache, large);
            if (cache.Hits > hitsBefore)
            {
                answered++;
            }

            // Space the attempts out. The store schedules a compaction of its
            // own on the pool, and attempts fired back to back would all race
            // the same pending pass: the run would then measure the race and
            // not the policy.
            Thread.Sleep(200);
        }

        answered.ShouldBeGreaterThan(
            0, "uma fonte acima da fatia de compactação precisa entrar e responder de memória");
    }

    /// <summary>
    /// What a published render does with one source: ask the memoization, and
    /// hand back what it parsed once the render over it finished. Returns what
    /// the entry weighs.
    /// </summary>
    private static long Memoize(ScribanParseCache cache, string source)
    {
        Template parsed = cache.GetOrParse(source);
        cache.Keep(source, parsed);
        return ScribanParseCache.WeightOf(source, parsed);
    }

    /// <summary>
    /// Loads the budget until the arriving weight no longer fits, which is the
    /// state the admission gate is exercised in.
    /// </summary>
    private static void Fill(ScribanParseCache cache, long arriving)
    {
        var filler = 0;
        while (cache.ResidentBytes + arriving <= ScribanParseCache.MaxResidentBytes)
        {
            Memoize(cache, DenseSource(FillerChars, filler));
            filler++;
        }
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
            Form(form).CopyTo(sources, form * SourcesPerForm);
        }

        return sources;
    }

    /// <summary>The five sources of one form.</summary>
    private static string[] Form(int form)
    {
        var mark = form.ToString(CultureInfo.InvariantCulture);
        return
        [
            "Pedido {{ order.id }} atualizado, aviso " + mark,
            "<p>Olá {{ user.name }}, o pedido {{ order.id }} tem {{ order.items.size }} itens "
                + "no aviso " + mark + ".</p>"
                + "<ul>{{ for item in order.items }}<li>{{ item.label }}: {{ item.qty }}</li>{{ end }}</ul>",
            "Olá {{ user.name }}, o pedido {{ order.id }} foi atualizado no aviso " + mark + ".",
            "<html><header>MB " + mark + "</header>{{ content }}<footer>rodapé</footer></html>",
            "MB " + mark + "\n{{ content }}\nrodapé",
        ];
    }

    /// <summary>
    /// One source of exactly the given length spent almost entirely on
    /// expressions, which is the heaviest tree an author buys per character
    /// without nesting anything.
    /// </summary>
    private static string DenseSource(int chars, int mark)
    {
        const string Unit = "{{a.b}}";
        const int MarkChars = 8;
        var builder = new StringBuilder(chars);
        while (builder.Length + Unit.Length + MarkChars <= chars)
        {
            builder.Append(Unit);
        }

        builder.Append(mark.ToString("D8", CultureInfo.InvariantCulture));
        return builder.Append('.', chars - builder.Length).ToString();
    }
}
