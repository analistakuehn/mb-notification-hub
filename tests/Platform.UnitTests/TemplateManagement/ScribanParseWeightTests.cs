using System.Globalization;
using System.Text;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using Scriban;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// Runs alone. Every claim here is a reading of the whole managed heap, and a
/// test class allocating next to it would be charged to the memoization.
/// </summary>
[CollectionDefinition(nameof(ManagedMemoryMeasurements), DisableParallelization = true)]
public sealed class ManagedMemoryMeasurements;

/// <summary>
/// What the parse memoization actually holds. The budget exists to bound
/// memory, and the source text is not what an entry weighs: the parsed tree is,
/// and how much tree one character buys is the author's choice.
/// </summary>
[Collection(nameof(ManagedMemoryMeasurements))]
public sealed class ScribanParseWeightTests
{
    /// <summary>
    /// Spread the comparison tolerates. It is wide on purpose: the claim is
    /// that the two budgets are the same order of magnitude, and the failure it
    /// exists to catch is measured in factors of eighty, not in percent.
    /// </summary>
    private const double ToleratedSpread = 2.0;

    /// <summary>
    /// Spread the reading of one node tolerates against the constant the budget
    /// is computed with. Measured on the same machine the constant came from,
    /// the shapes this module renders span 152 to 170 bytes per node, so a
    /// quarter is four times the spread of an honest reading and still an order
    /// of magnitude below what a change of node layout would produce.
    /// </summary>
    private const double NodeWeightSpread = 0.25;

    /// <summary>Near the ceiling one template source may carry.</summary>
    private const int SourceChars = 131_000;

    /// <summary>Movement between two readings that counts as a settled heap.</summary>
    private const long SettledSlackBytes = 1_000_000;

    [Fact]
    public void A_budget_of_dense_sources_holds_no_more_managed_memory_than_a_budget_of_plain_ones()
    {
        var plain = RetainedBytes(Plain);
        var dense = RetainedBytes(Dense);

        // Neither budget may be empty, or the comparison would pass on a
        // memoization that simply refused everything it was offered.
        plain.ShouldBeGreaterThan(0);
        dense.ShouldBeGreaterThan(0);

        var reading = string.Create(
            CultureInfo.InvariantCulture,
            $"o mesmo orçamento reteve {dense:N0} bytes em fontes densas contra {plain:N0} em simples");
        dense.ShouldBeLessThan((long)(plain * ToleratedSpread), reading);
    }

    [Fact]
    public void The_weight_of_a_parsed_node_stays_within_the_measured_band()
    {
        const int Copies = 60;
        const int Chars = 5_000;
        var sources = new string[Copies];
        for (var index = 0; index < Copies; index++)
        {
            sources[index] = Dense(index, Chars);
        }

        // A discarded parse first, so the statics the parser fills on its way up
        // are not charged to the reading.
        ScribanParseCache.CountNodes(Template.Parse(sources[0]));

        var before = SettledBytes();
        var trees = new Template[Copies];
        for (var index = 0; index < Copies; index++)
        {
            trees[index] = Template.Parse(sources[index]);
        }

        var after = SettledBytes();
        var nodes = 0L;
        foreach (Template tree in trees)
        {
            nodes += ScribanParseCache.CountNodes(tree);
        }

        GC.KeepAlive(trees);

        // Enough nodes that the fixed cost of a template is noise, or the
        // reading would be about the template and not about the node.
        nodes.ShouldBeGreaterThan(100_000);

        var measured = (after - before) / (double)nodes;
        var reading = string.Create(
            CultureInfo.InvariantCulture,
            $"um nó pesa {measured:0.0} bytes e o orçamento conta {ScribanParseCache.BytesPerNode}");
        measured.ShouldBeInRange(
            ScribanParseCache.BytesPerNode * (1 - NodeWeightSpread),
            ScribanParseCache.BytesPerNode * (1 + NodeWeightSpread),
            reading);
    }

    /// <summary>
    /// Managed bytes the memoization holds once its budget is loaded with one
    /// shape of source. The sources are built inside the loop and dropped, so
    /// what the reading charges is what the store kept alive.
    /// </summary>
    private static long RetainedBytes(Func<int, string> shape)
    {
        var probe = shape(0);
        var weight = ScribanParseCache.WeightOf(probe, Template.Parse(probe));

        var before = SettledBytes();
        using var cache = new ScribanParseCache();
        var filler = 0;
        do
        {
            var source = shape(filler);
            Template parsed = cache.GetOrParse(source);
            cache.Keep(source, parsed);
            filler++;
        }
        while (cache.ResidentBytes + weight <= ScribanParseCache.MaxResidentBytes);

        var after = SettledBytes();
        GC.KeepAlive(cache);
        return after - before;
    }

    /// <summary>
    /// Managed bytes with the heap settled. One collection is not enough: a
    /// full pass taken right after a large graph became unreachable still
    /// reports it and only the next pass shows it gone, so a reading taken on
    /// the first one charges the previous measurement's store to this one.
    /// </summary>
    private static long SettledBytes()
    {
        var previous = GC.GetTotalMemory(forceFullCollection: true);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var current = GC.GetTotalMemory(forceFullCollection: true);
            if (Math.Abs(current - previous) <= SettledSlackBytes)
            {
                return current;
            }

            previous = current;
        }

        return previous;
    }

    /// <summary>Text and nothing else: the cheapest tree a source can produce.</summary>
    private static string Plain(int mark)
        => new string('.', SourceChars - 8) + mark.ToString("D8", CultureInfo.InvariantCulture);

    /// <summary>
    /// The same number of characters spent entirely on expressions, which is
    /// the densest tree an author can write without nesting anything.
    /// </summary>
    private static string Dense(int mark) => Dense(mark, SourceChars);

    private static string Dense(int mark, int chars)
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
