using System.Diagnostics;
using System.Globalization;
using NotificationHub.PerformanceTests.Contention;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>What one arm of the memoization probe measured.</summary>
internal sealed record MemoizationArm(
    string ArmId,
    int Workers,
    int KeySpace,
    int Ceiling,
    long Operations,
    double ElapsedSeconds,
    double OperationsPerSecond,
    long Contentions,
    double ContentionsPerThousand,
    double HitShare,
    int ResidentAtEnd,
    int ResidentMax);

/// <summary>Everything one memoization run produced.</summary>
internal sealed record MemoizationOutcome(
    string RecordedAtUtc,
    string Host,
    int Processors,
    string Runtime,
    IReadOnlyList<MemoizationArm> Arms);

/// <summary>
/// What the published-read memoization costs when many threads miss at once
/// with its budget already full.
/// <para>
/// The budget being full is the whole point: it is the only state where the
/// eviction policy runs, and it is the state a burst of distinct template keys
/// puts the process in. The probe reports two things the deployed policy has to
/// hold at the same time, throughput under concurrent misses and a resident set
/// that never passes the budget, because a policy that keeps up by letting the
/// set grow is a leak that every in-process test would call a pass.
/// </para>
/// </summary>
internal static class PublishedReadMemoizationScenario
{
    /// <summary>The arm that reports throughput and contention; nothing observes it while it runs.</summary>
    internal const string ThroughputArm = "M1";

    /// <summary>The arm that watches the resident set; the observer's own locks stay out of M1.</summary>
    internal const string BoundArm = "M2";

    private const string Value = "published-read-value";

    private static readonly TimeSpan ObserverPeriod = TimeSpan.FromMilliseconds(20);

    internal static MemoizationOutcome Run(int workers, TimeSpan duration, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);

        using PublishedReadCacheHandle cache = PublishedReadCacheHandle.Create();

        // The key space sits just above the budget, so the arm lives on the
        // boundary: nearly every operation misses, writes, and meets the gate.
        var keySpace = cache.Ceiling + (cache.Ceiling / 8);
        var keys = Keys(keySpace);

        // A discarded pass first. It fills the budget and pays for the cold
        // buffers, the cold plan of the delegates and the tiered recompilation,
        // all of which would otherwise land whole on the first measured arm.
        Drive(cache, keys, workers, TimeSpan.FromSeconds(2), observe: false);

        report($"Braço {ThroughputArm}: {workers} threads sobre {keySpace:N0} chaves, teto {cache.Ceiling:N0}.");
        MemoizationArm throughput = Measure(ThroughputArm, cache, keys, workers, duration, observe: false);
        Describe(throughput, report);

        report($"Braço {BoundArm}: mesma carga com observador do residente a cada {ObserverPeriod.TotalMilliseconds:N0} ms.");
        MemoizationArm bound = Measure(BoundArm, cache, keys, workers, duration, observe: true);
        Describe(bound, report);

        return new MemoizationOutcome(
            DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            Environment.MachineName,
            Environment.ProcessorCount,
            Environment.Version.ToString(),
            [throughput, bound]);
    }

    private static void Describe(MemoizationArm arm, Action<string> report)
        => report(string.Create(
            CultureInfo.InvariantCulture,
            $"  {arm.Operations:N0} operações, {arm.OperationsPerSecond:N0} op/s, "
            + $"{arm.ContentionsPerThousand:0.000} disputas de lock por mil, "
            + $"acerto {arm.HitShare:P1}, residente máximo {arm.ResidentMax:N0} de {arm.Ceiling:N0}."));

    private static string[] Keys(int keySpace)
    {
        var keys = new string[keySpace];
        for (var index = 0; index < keySpace; index++)
        {
            keys[index] = string.Create(CultureInfo.InvariantCulture, $"template:araia-cambio:key-{index}");
        }

        return keys;
    }

    private static MemoizationArm Measure(
        string armId,
        PublishedReadCacheHandle cache,
        string[] keys,
        int workers,
        TimeSpan duration,
        bool observe)
    {
        var hitsBefore = cache.PointerHits;
        var loadsBefore = cache.PointerLoads;
        var contentionsBefore = Monitor.LockContentionCount;
        var started = Stopwatch.GetTimestamp();

        (var operations, var residentMax) = Drive(cache, keys, workers, duration, observe);

        var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        var contentions = Monitor.LockContentionCount - contentionsBefore;
        var hits = cache.PointerHits - hitsBefore;
        var loads = cache.PointerLoads - loadsBefore;
        var lookups = hits + loads;
        return new MemoizationArm(
            armId,
            workers,
            keys.Length,
            cache.Ceiling,
            operations,
            elapsed,
            elapsed > 0 ? operations / elapsed : double.NaN,
            contentions,
            operations > 0 ? contentions * 1000d / operations : double.NaN,
            lookups > 0 ? hits / (double)lookups : double.NaN,
            cache.PointerCount,
            observe ? residentMax : cache.PointerCount);
    }

    /// <summary>
    /// Runs the arm and returns what it did. Each worker walks the whole key
    /// space from its own offset, so the workers collide on live keys instead of
    /// owning disjoint slices, which is the shape a burst of producers makes.
    /// </summary>
    private static (long Operations, int ResidentMax) Drive(
        PublishedReadCacheHandle cache,
        string[] keys,
        int workers,
        TimeSpan duration,
        bool observe)
    {
        using var stopping = new CancellationTokenSource(duration);
        var performed = new long[workers];
        var residentMax = 0;
        Thread? observer = null;
        if (observe)
        {
            observer = new Thread(() =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    var resident = cache.PointerCount;
                    if (resident > residentMax)
                    {
                        residentMax = resident;
                    }

                    Thread.Sleep(ObserverPeriod);
                }
            })
            { IsBackground = true, Name = "memoization-observer" };
            observer.Start();
        }

        Thread[] threads = new Thread[workers];
        for (var worker = 0; worker < workers; worker++)
        {
            var index = worker;
            threads[index] = new Thread(() =>
            {
                var cursor = index * (keys.Length / workers);
                var operations = 0L;
                while (!stopping.IsCancellationRequested)
                {
                    var key = keys[cursor];
                    if (!cache.TryGetPointer(key, out _))
                    {
                        cache.SetPointer(key, Value);
                    }

                    operations++;
                    cursor = cursor + 1 == keys.Length ? 0 : cursor + 1;
                }

                performed[index] = operations;
            })
            { IsBackground = true, Name = $"memoization-worker-{index}" };
            threads[index].Start();
        }

        foreach (Thread thread in threads)
        {
            thread.Join();
        }

        observer?.Join();
        var resident = cache.PointerCount;
        return (performed.Sum(), Math.Max(residentMax, resident));
    }
}
