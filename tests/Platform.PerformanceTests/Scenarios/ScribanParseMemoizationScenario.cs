using System.Diagnostics;
using System.Globalization;
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
    long ResidentChars);

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
/// The arm has no cold tail on purpose: every source it asks for was parsed
/// during the discarded pass, so the only thing left to measure is what the
/// lookup itself costs and whether the catalogue is still there at the end.
/// That second answer is the one that moves with the eviction policy. A policy
/// that bounds the memoization by counting entries drops a catalogue of this
/// size while it is being read, and the arm then pays a parse for sources it
/// had already parsed, over and over.
/// </para>
/// </summary>
internal static class ScribanParseMemoizationScenario
{
    /// <summary>The single arm: the whole catalogue hot, nothing else offered.</summary>
    internal const string HotArm = "S1";

    /// <summary>Subject, body, text body and the two layout frames around them.</summary>
    private const int SourcesPerForm = 5;

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

        using ScribanParseCacheHandle cache = ScribanParseCacheHandle.Create();
        var sources = FormSources(forms);
        var offered = sources.Sum(source => (long)source.Length);

        // A discarded pass first. It parses every source once, so the measured
        // passes start with the catalogue whole, and it pays for the cold
        // buffers, the cold plan of the delegates and the tiered recompilation,
        // all of which would otherwise land on the first measured pass.
        Drive(cache, sources, workers, TimeSpan.FromSeconds(2));

        report(string.Create(
            CultureInfo.InvariantCulture,
            $"Braço {HotArm}: {workers} threads sobre {forms:N0} formas, "
            + $"{sources.Length:N0} fontes, {offered:N0} de {cache.Budget:N0} caracteres, "
            + $"{passes} passagens."));

        var measured = new List<ParseMemoizationArm>();
        for (var pass = 0; pass < passes; pass++)
        {
            ParseMemoizationArm result = Measure(HotArm, cache, sources, offered, workers, duration);
            Describe(result, report);
            measured.Add(result);
        }

        // The median of the passes, never a single one: a reference taken on a
        // lucky pass leaves the next honest run without margin, and one taken on
        // an unlucky pass buys silence. Two terms are deliberately not the
        // median's. The reparses are the sum over every pass, because one pass
        // that had to parse a source it already held is a failure whether or not
        // its throughput happened to sit in the middle; and the resident set is
        // read once at the end, which is the only moment the claim is about.
        measured.Sort((left, right) => left.OperationsPerSecond.CompareTo(right.OperationsPerSecond));
        ParseMemoizationArm hot = measured[measured.Count / 2] with
        {
            Parses = measured.Sum(arm => arm.Parses),
            ResidentChars = cache.ResidentChars,
        };
        report(string.Create(
            CultureInfo.InvariantCulture,
            $"  mediana: {hot.OperationsPerSecond / 1_000_000:N2} Mops/s, "
            + $"{hot.Parses:N0} reparses somados, residente {hot.ResidentChars:N0} caracteres."));

        return new ParseMemoizationOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment.MachineName,
            Environment.ProcessorCount,
            Environment.Version.ToString(),
            [hot]);
    }

    private static void Describe(ParseMemoizationArm arm, Action<string> report)
        => report(string.Create(
            CultureInfo.InvariantCulture,
            $"  {arm.Operations:N0} buscas, {arm.OperationsPerSecond / 1_000_000:N2} Mops/s, "
            + $"{arm.ContentionsPerThousand:0.000} disputas de lock por mil, "
            + $"{arm.Parses:N0} reparses, residente {arm.ResidentChars:N0} caracteres."));

    private static ParseMemoizationArm Measure(
        string armId,
        ScribanParseCacheHandle cache,
        string[] sources,
        long offered,
        int workers,
        TimeSpan duration)
    {
        var parsesBefore = cache.Parses;
        var contentionsBefore = Monitor.LockContentionCount;
        var started = Stopwatch.GetTimestamp();

        var operations = Drive(cache, sources, workers, duration);

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var contentions = Monitor.LockContentionCount - contentionsBefore;
        return new ParseMemoizationArm(
            armId,
            workers,
            sources.Length / SourcesPerForm,
            sources.Length,
            offered,
            cache.Budget,
            operations,
            elapsed,
            elapsed > 0 ? operations / elapsed : double.NaN,
            contentions,
            operations > 0 ? contentions * 1000d / operations : double.NaN,
            cache.Parses - parsesBefore,
            cache.ResidentChars);
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
}
