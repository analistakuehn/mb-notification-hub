using System.Diagnostics;
using Npgsql;
using NotificationHub.PerformanceTests.Instrumentation;

namespace NotificationHub.PerformanceTests.Contention;

/// <summary>What one arm produced at one volume.</summary>
internal sealed record ArmResult(
    string ArmId,
    string Question,
    int Volume,
    int Appenders,
    int Transactions,
    int TrailRows,
    int Failures,
    string? FailureDiagnosis,
    double ElapsedSeconds,
    double AppendsPerSecond,
    PhaseStatistics Setup,
    PhaseStatistics Wait,
    PhaseStatistics PreCommit,
    PhaseStatistics Commit,
    PhaseStatistics Hold,
    PhaseStatistics Window,
    PhaseStatistics Latency,
    IReadOnlyDictionary<string, PhaseStatistics> HoldByOperation,
    IReadOnlyList<WaitEventTally> WaitEvents);

/// <summary>
/// Drives one arm: every appender loops as fast as it can until the arm's time
/// budget or append budget runs out. The load is closed-loop on purpose. At the
/// volumes that matter the partition ceiling falls below the projected demand,
/// and an open-loop driver at that point measures the length of its own queue
/// instead of the cost of an append.
/// </summary>
internal static class ArmRunner
{
    private const int WarmupSamplesPerAppender = 5;

    internal static async Task<ArmResult> RunAsync(
        NpgsqlDataSource dataSource,
        ContentionArm arm,
        int volume,
        TimeSpan duration,
        int maxTransactions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arm);
        var appender = new AuditAppender(dataSource, arm.Shape);
        using var samplerStop = new CancellationTokenSource();
        var sampler = new WaitEventSampler(dataSource, TimeSpan.FromMilliseconds(20));
        Task<IReadOnlyList<WaitEventTally>> sampling = sampler.RunAsync(samplerStop.Token);

        int[] budget = [0];
        int[] failures = [0];
        string?[] diagnosis = [null];
        var started = Stopwatch.GetTimestamp();
        var deadline = started + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        var collected = new List<(string Operation, AppendSample Sample, int Rows)>[arm.Appenders.Count];

        var workers = new Task[arm.Appenders.Count];
        for (var index = 0; index < arm.Appenders.Count; index++)
        {
            var slot = index;
            collected[slot] = [];
            workers[slot] = Task.Run(
                async () =>
                {
                    AppenderSpec spec = arm.Appenders[slot];
                    var random = new Random(9973 + slot);
                    var salt = slot * 1_000_003;
                    while (Stopwatch.GetTimestamp() < deadline
                        && Interlocked.Increment(ref budget[0]) <= maxTransactions)
                    {
                        AppendOperation operation = Draw(spec.Mixture, random);
                        try
                        {
                            AppendSample sample = await appender.AppendAsync(
                                spec.Partition, operation, salt++, cancellationToken);
                            collected[slot].Add((operation.Name, sample, operation.AppendsPerTransaction));
                        }
                        catch (NpgsqlException failure)
                        {
                            // A refused append is data, not an accident: at the
                            // volumes where the ceiling collapses the queue
                            // outlives the command, and losing the whole arm to
                            // it would hide the very number the arm exists for.
                            Interlocked.Increment(ref failures[0]);
                            Interlocked.CompareExchange(ref diagnosis[0], failure.Message, null);
                        }
                    }
                },
                cancellationToken);
        }

        double elapsed;
        try
        {
            await Task.WhenAll(workers);
        }
        finally
        {
            elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            await samplerStop.CancelAsync();
        }

        IReadOnlyList<WaitEventTally> waitEvents = await sampling;

        var setup = new LatencyHistogram();
        var wait = new LatencyHistogram();
        var preCommit = new LatencyHistogram();
        var commit = new LatencyHistogram();
        var hold = new LatencyHistogram();
        var window = new LatencyHistogram();
        var latency = new LatencyHistogram();
        Dictionary<string, LatencyHistogram> byOperation = [];
        var rows = 0;
        var transactions = 0;

        foreach (List<(string Operation, AppendSample Sample, int Rows)> perAppender in collected)
        {
            // Discarding five samples of six would leave one, and one sample is
            // not a percentile. Where the cell is slow the warmup shrinks with it.
            var warmup = Math.Min(WarmupSamplesPerAppender, perAppender.Count / 4);
            for (var index = warmup; index < perAppender.Count; index++)
            {
                (var operation, AppendSample sample, var appended) = perAppender[index];
                setup.Add(sample.SetupMs);
                wait.Add(sample.WaitMs);
                preCommit.Add(sample.PreCommitMs);
                commit.Add(sample.CommitMs);
                hold.Add(sample.HoldMs);
                window.Add(sample.WaitMs + sample.HoldMs);
                latency.Add(sample.LatencyMs);
                if (!byOperation.TryGetValue(operation, out LatencyHistogram? operationHold))
                {
                    operationHold = new LatencyHistogram();
                    byOperation[operation] = operationHold;
                }

                operationHold.Add(sample.HoldMs);
                rows += appended;
                transactions++;
            }
        }

        return new ArmResult(
            arm.Id,
            arm.Question,
            volume,
            arm.Appenders.Count,
            transactions,
            rows,
            failures[0],
            diagnosis[0],
            elapsed,
            elapsed > 0 ? rows / elapsed : double.NaN,
            setup.Snapshot(),
            wait.Snapshot(),
            preCommit.Snapshot(),
            commit.Snapshot(),
            hold.Snapshot(),
            window.Snapshot(),
            latency.Snapshot(),
            byOperation.ToDictionary(entry => entry.Key, entry => entry.Value.Snapshot(), StringComparer.Ordinal),
            waitEvents);
    }

    private static AppendOperation Draw(IReadOnlyList<AppendOperation> mixture, Random random)
    {
        if (mixture.Count == 1)
        {
            return mixture[0];
        }

        var total = mixture.Sum(operation => operation.Weight);
        var draw = random.Next(total);
        foreach (AppendOperation operation in mixture)
        {
            draw -= operation.Weight;
            if (draw < 0)
            {
                return operation;
            }
        }

        return mixture[^1];
    }
}
