using System.Diagnostics;
using Npgsql;
using NotificationHub.PerformanceTests.Contention;
using NotificationHub.PerformanceTests.Infrastructure;
using NotificationHub.PerformanceTests.Instrumentation;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>The open-loop cell: an offered rate, and what the partition did with it.</summary>
internal sealed record SustainedRateResult(
    int Volume,
    int OfferedRate,
    double Seconds,
    int Offered,
    int Completed,
    int Refused,
    double AchievedRate,
    PhaseStatistics Window,
    PhaseStatistics Latency)
{
    /// <summary>The queue diverged: arrivals outran the partition and slots ran out.</summary>
    internal bool Diverged => Refused > 0;
}

/// <summary>
/// Offers appends at a fixed rate instead of as fast as the appenders can go.
/// This is the only shape that can answer the sub-budget question directly,
/// because a p99 measured at saturation is a property of the driver's queue,
/// not of the append. When the offered rate is above the ceiling the answer is
/// still decisive: the in-flight slots run out and the cell reports divergence
/// rather than a number that looks like a latency.
/// </summary>
internal static class SustainedRateScenario
{
    /// <summary>
    /// Kept below the connection pool on purpose. A slot that outruns the pool
    /// does not measure the partition, it measures the pool's own queue, and
    /// past the pool timeout it stops measuring anything and throws.
    /// </summary>
    private const int MaxInFlight = 48;

    internal static async Task<SustainedRateResult> RunAsync(
        NpgsqlDataSource dataSource,
        PartitionMonth month,
        AppendShape shape,
        IReadOnlyList<AppendOperation> mixture,
        int volume,
        int offeredRate,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mixture);
        var appender = new AuditAppender(dataSource, shape);
        var random = new Random(104729);
        using var slots = new SemaphoreSlim(MaxInFlight, MaxInFlight);
        var samples = new List<AppendSample>();
        var pending = new List<Task>();
        var refused = 0;
        var offered = 0;
        var total = (int)(offeredRate * duration.TotalSeconds);
        var interval = TimeSpan.FromSeconds(1.0 / offeredRate);
        var started = Stopwatch.GetTimestamp();

        for (var index = 0; index < total; index++)
        {
            TimeSpan due = interval * index;
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            if (due > elapsed)
            {
                await Task.Delay(due - elapsed, cancellationToken);
            }

            offered++;
            if (!await slots.WaitAsync(TimeSpan.Zero, cancellationToken))
            {
                refused++;
                continue;
            }

            AppendOperation operation = Draw(mixture, random);
            var salt = index;
            pending.Add(Task.Run(
                async () =>
                {
                    try
                    {
                        AppendSample sample = await appender.AppendAsync(
                            month, operation, salt, cancellationToken);
                        lock (samples)
                        {
                            samples.Add(sample);
                        }
                    }
                    finally
                    {
                        slots.Release();
                    }
                },
                cancellationToken));
        }

        await Task.WhenAll(pending);
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;

        var window = new LatencyHistogram();
        var latency = new LatencyHistogram();
        foreach (AppendSample sample in samples)
        {
            window.Add(sample.WaitMs + sample.HoldMs);
            latency.Add(sample.LatencyMs);
        }

        return new SustainedRateResult(
            volume,
            offeredRate,
            seconds,
            offered,
            samples.Count,
            refused,
            seconds > 0 ? samples.Count / seconds : double.NaN,
            window.Snapshot(),
            latency.Snapshot());
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
