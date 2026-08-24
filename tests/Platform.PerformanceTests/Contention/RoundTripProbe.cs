using System.Diagnostics;
using Npgsql;
using NotificationHub.PerformanceTests.Instrumentation;

namespace NotificationHub.PerformanceTests.Contention;

/// <summary>
/// The cost of one trivial round trip to the database, measured in the same
/// run, at the same concurrency, and at more than one moment of the run.
/// </summary>
/// <remarks>
/// This exists because the guard metric of the pull-request gate cannot be an
/// absolute latency: on a busy host the same arm produced medians thirty per
/// cent apart between runs of the same code, and widening the tolerance until
/// that stops failing buys silence, not signal. Dividing the hold window by a
/// round trip cancels the host and leaves the shape, which is how many round
/// trips the append holds the lock for.
///
/// The yardstick has to be measured the way the arms are measured. A single
/// connection in a tight loop reports the client's scheduling luck of that
/// moment: measured that way it moved 49 % between two runs whose hold windows
/// moved 4 %, so dividing made the metric worse instead of better. Same worker
/// count, own connection per worker, sampled before and after the arms.
/// </remarks>
internal static class RoundTripProbe
{
    private const string TrivialSql = "SELECT 1";

    private const int WarmupCalls = 20;

    internal static async Task SampleAsync(
        NpgsqlDataSource dataSource,
        LatencyHistogram histogram,
        int workers,
        int samplesPerWorker,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(histogram);

        var collected = new List<double>[workers];
        var tasks = new Task[workers];
        for (var index = 0; index < workers; index++)
        {
            var slot = index;
            collected[slot] = new List<double>(samplesPerWorker);
            tasks[slot] = Task.Run(
                async () =>
                {
                    await using NpgsqlConnection connection =
                        await dataSource.OpenConnectionAsync(cancellationToken);
                    await using NpgsqlCommand command = connection.CreateCommand();
                    command.CommandText = TrivialSql;

                    // The opening calls pay for the first parse and the first
                    // buffer of the connection, which is not what the yardstick
                    // is about.
                    for (var warmup = 0; warmup < WarmupCalls; warmup++)
                    {
                        await command.ExecuteScalarAsync(cancellationToken);
                    }

                    for (var sample = 0; sample < samplesPerWorker; sample++)
                    {
                        var start = Stopwatch.GetTimestamp();
                        await command.ExecuteScalarAsync(cancellationToken);
                        collected[slot].Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    }
                },
                cancellationToken);
        }

        await Task.WhenAll(tasks);
        foreach (List<double> samples in collected)
        {
            foreach (var sample in samples)
            {
                histogram.Add(sample);
            }
        }
    }
}
