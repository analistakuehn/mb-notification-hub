using System.Diagnostics;
using System.Globalization;
using System.Text;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>What one arm of the parse memoization probe measured.</summary>
internal sealed record ParseMemoizationArm(
    string ArmId,
    int Workers,
    int Forms,
    int Sources,
    long OfferedChars,
    long Budget,
    long Operations,
    double ElapsedSeconds,
    double OperationsPerSecond,
    long Contentions,
    double ContentionsPerThousand,
    long Parses,
    long ResidentBytes,
    long ResidentEntries,
    long? LargeSourceHits);

/// <summary>Everything one parse memoization run produced.</summary>
internal sealed record ParseMemoizationOutcome(
    string RecordedAtUtc,
    string Host,
    int Processors,
    string Runtime,
    IReadOnlyList<ParseMemoizationArm> Arms);

/// <summary>
/// What the parse memoization costs when every thread on the machine reads a
/// published catalogue that is already in memory.
/// <para>
/// Neither arm has a cold tail on purpose: every source they ask for was loaded
/// before the discarded pass, so the only thing left to measure is what the
/// lookup itself costs and whether the catalogue is still there at the end.
/// That second answer is the one that moves with the eviction policy. A policy
/// that bounds the memoization by counting entries drops a catalogue of this
/// size while it is being read, and the arm then pays a parse for sources it
/// had already parsed, over and over.
/// </para>
/// <para>
/// The second arm exists because the first one cannot reach the admission gate:
/// its sources are small and its budget is empty, and a store with room takes
/// everything it is offered. So the second arm loads the budget first and puts
/// one source heavier than a compaction pass in the hot set, which is the shape
/// that a policy freeing a fixed share of the budget refuses forever.
/// </para>
/// </summary>
internal static class ScribanParseMemoizationScenario
{
    /// <summary>The whole catalogue hot in an empty budget, nothing else offered.</summary>
    internal const string HotArm = "S1";

    /// <summary>The same catalogue plus one heavy source, in a budget already full.</summary>
    internal const string LoadedArm = "S2";

    /// <summary>Subject, body, text body and the two layout frames around them.</summary>
    private const int SourcesPerForm = 5;

    /// <summary>
    /// One source of the ballast that loads the budget of the second arm. Small
    /// enough that what is left over at the end is a rounding error, and heavy
    /// enough that loading the budget costs dozens of parses and not thousands.
    /// </summary>
    private const int BallastChars = 5_000;

    /// <summary>
    /// The heavy source of the second arm. Its parsed tree outweighs what one
    /// compaction pass frees, which is what makes it the source a fixed-share
    /// policy stops admitting once the budget is full.
    /// </summary>
    private const int HeavyChars = 25_000;

    internal static ParseMemoizationOutcome Run(
        int workers,
        int forms,
        int passes,
        TimeSpan duration,
        Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(forms, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(passes, 1);

        return new ParseMemoizationOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment.MachineName,
            Environment.ProcessorCount,
            Environment.Version.ToString(),
            [
                Hot(new ArmPlan(HotArm, FormSources(forms), null, workers, passes, duration), report),
                Loaded(new ArmPlan(
                    LoadedArm, FormSources(forms), Dense(HeavyChars, 0), workers, passes, duration), report),
            ]);
    }

    /// <summary>The catalogue alone, in a store that has room for all of it.</summary>
    private static ParseMemoizationArm Hot(ArmPlan plan, Action<string> report)
    {
        using ScribanParseCacheHandle cache = ScribanParseCacheHandle.Create();
        return Measure(plan, cache, report);
    }

    /// <summary>
    /// The catalogue and one heavy source, in a store loaded to the brim by
    /// content nobody in the arm ever asks for again. The ballast is what puts
    /// the admission gate in the path of the hot set.
    /// </summary>
    private static ParseMemoizationArm Loaded(ArmPlan plan, Action<string> report)
    {
        using ScribanParseCacheHandle cache = ScribanParseCacheHandle.Create();
        var before = cache.ResidentBytes;
        cache.Load(Dense(BallastChars, 0));
        var weight = cache.ResidentBytes - before;
        var filler = 1;
        while (cache.ResidentBytes + weight <= cache.Budget)
        {
            cache.Load(Dense(BallastChars, filler));
            filler++;
        }

        report(string.Create(
            CultureInfo.InvariantCulture,
            $"Lastro do braço {plan.ArmId}: {filler:N0} fontes, "
            + $"{cache.ResidentBytes:N0} de {cache.Budget:N0} bytes residentes."));
        return Measure(plan, cache, report);
    }

    private static ParseMemoizationArm Measure(ArmPlan plan, ScribanParseCacheHandle cache, Action<string> report)
    {
        var sources = plan.HotSet();
        var offered = sources.Sum(source => (long)source.Length);

        // Every source of the hot set is loaded the way a published render
        // loads it, and only then measured. A lookup that had to parse after
        // this is a source the policy threw away while it was being read.
        foreach (var source in sources)
        {
            cache.Load(source);
        }

        // A discarded pass next. It pays for the cold buffers, the cold plan of
        // the delegates and the tiered recompilation, all of which would
        // otherwise land on the first measured pass.
        Drive(cache, sources, plan.Workers, TimeSpan.FromSeconds(2));

        report(string.Create(
            CultureInfo.InvariantCulture,
            $"Braço {plan.ArmId}: {plan.Workers} threads sobre {plan.Forms():N0} formas, "
            + $"{sources.Length:N0} fontes, {offered:N0} caracteres, "
            + $"{cache.ResidentBytes:N0} de {cache.Budget:N0} bytes residentes, "
            + $"{plan.Passes} passagens."));

        var measured = new List<ParseMemoizationArm>();
        for (var pass = 0; pass < plan.Passes; pass++)
        {
            ParseMemoizationArm result = Pass(plan, cache, sources, offered);
            Describe(result, report);
            measured.Add(result);
        }

        // The median of the passes, never a single one: a reference taken on a
        // lucky pass leaves the next honest run without margin, and one taken on
        // an unlucky pass buys silence. Three terms are deliberately not the
        // median's. The reparses are the sum over every pass, because one pass
        // that had to parse a source it already held is a failure whether or not
        // its throughput happened to sit in the middle; and the resident set and
        // the heavy source are read once at the end, which is the only moment
        // those claims are about.
        measured.Sort((left, right) => left.OperationsPerSecond.CompareTo(right.OperationsPerSecond));
        ParseMemoizationArm arm = measured[measured.Count / 2] with
        {
            Parses = measured.Sum(pass => pass.Parses),
            ResidentBytes = cache.ResidentBytes,
            ResidentEntries = cache.ResidentEntries,
            LargeSourceHits = Answered(cache, plan.Large),
        };
        report(string.Create(
            CultureInfo.InvariantCulture,
            $"  mediana: {arm.OperationsPerSecond / 1_000_000:N2} Mops/s, "
            + $"{arm.Parses:N0} reparses somados, residente {arm.ResidentBytes:N0} bytes "
            + $"em {arm.ResidentEntries:N0} entradas."));
        return arm;
    }

    /// <summary>
    /// Whether the heavy source is still answerable once the passes are over.
    /// The entry count cannot say it: the ballast pads it, and the one source
    /// the arm exists for would go missing without moving the total.
    /// </summary>
    private static long? Answered(ScribanParseCacheHandle cache, string? large)
    {
        if (large is null)
        {
            return null;
        }

        var before = cache.Hits;
        cache.GetOrParse(large);
        return cache.Hits - before;
    }

    private static void Describe(ParseMemoizationArm arm, Action<string> report)
        => report(string.Create(
            CultureInfo.InvariantCulture,
            $"  {arm.Operations:N0} buscas, {arm.OperationsPerSecond / 1_000_000:N2} Mops/s, "
            + $"{arm.ContentionsPerThousand:0.000} disputas de lock por mil, "
            + $"{arm.Parses:N0} reparses, residente {arm.ResidentBytes:N0} bytes."));

    private static ParseMemoizationArm Pass(
        ArmPlan plan,
        ScribanParseCacheHandle cache,
        string[] sources,
        long offered)
    {
        var parsesBefore = cache.Parses;
        var contentionsBefore = Monitor.LockContentionCount;
        var started = Stopwatch.GetTimestamp();

        var operations = Drive(cache, sources, plan.Workers, plan.Duration);

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var contentions = Monitor.LockContentionCount - contentionsBefore;
        return new ParseMemoizationArm(
            plan.ArmId,
            plan.Workers,
            plan.Forms(),
            sources.Length,
            offered,
            cache.Budget,
            operations,
            elapsed,
            elapsed > 0 ? operations / elapsed : double.NaN,
            contentions,
            operations > 0 ? contentions * 1000d / operations : double.NaN,
            cache.Parses - parsesBefore,
            cache.ResidentBytes,
            cache.ResidentEntries,
            null);
    }

    /// <summary>
    /// Runs the arm and returns how many lookups it made. Each worker walks the
    /// whole catalogue from its own offset, so the workers read the same sources
    /// at the same time instead of owning disjoint slices, which is the shape a
    /// burst of dispatches makes.
    /// </summary>
    private static long Drive(
        ScribanParseCacheHandle cache,
        string[] sources,
        int workers,
        TimeSpan duration)
    {
        using var stopping = new CancellationTokenSource(duration);
        var performed = new long[workers];
        Thread[] threads = new Thread[workers];
        for (var worker = 0; worker < workers; worker++)
        {
            var index = worker;
            threads[index] = new Thread(() =>
            {
                var cursor = index * (sources.Length / workers);
                var operations = 0L;
                while (!stopping.IsCancellationRequested)
                {
                    cache.GetOrParse(sources[cursor]);
                    operations++;
                    cursor = cursor + 1 == sources.Length ? 0 : cursor + 1;
                }

                performed[index] = operations;
            })
            { IsBackground = true, Name = $"parse-worker-{index}" };
            threads[index].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        return performed.Sum();
    }

    /// <summary>
    /// The five sources of each form, in the shape the deployed renderer works
    /// in, distinct per form the way a catalogue is distinct: same shape,
    /// different content.
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

    /// <summary>
    /// One source of exactly the given length spent almost entirely on
    /// expressions, which is the heaviest tree an author buys per character
    /// without nesting anything.
    /// </summary>
    private static string Dense(int chars, int mark)
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

    /// <summary>What one arm reads and under which conditions it reads it.</summary>
    private sealed record ArmPlan(
        string ArmId,
        string[] Catalog,
        string? Large,
        int Workers,
        int Passes,
        TimeSpan Duration)
    {
        /// <summary>The catalogue, and the heavy source when the arm carries one.</summary>
        internal string[] HotSet() => Large is null ? Catalog : [.. Catalog, Large];

        internal int Forms() => Catalog.Length / SourcesPerForm;
    }
}
