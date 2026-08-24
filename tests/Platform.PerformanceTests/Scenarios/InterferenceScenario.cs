using System.Diagnostics;
using Npgsql;
using NotificationHub.PerformanceTests.Contention;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Scenarios;

/// <summary>
/// The paired answer of the interference arm. The absolute cost of the purge
/// is not the question; whether it moves the tail of the append is.
/// </summary>
internal sealed record InterferenceResult(
    int Volume,
    int Marks,
    double PurgeSeconds,
    int PurgedRows,
    ArmResult Quiet,
    ArmResult WithPurge)
{
    internal double WindowP99Shift => WithPurge.Window.P99 - Quiet.Window.P99;

    internal double WindowP99Ratio => Quiet.Window.P99 > 0 ? WithPurge.Window.P99 / Quiet.Window.P99 : double.NaN;
}

/// <summary>
/// Runs the real mixture twice back to back at the same volume: once with the
/// database otherwise quiet, once while a purge round of the dedupe marks
/// sweeps the platform schema. Running the pair back to back is what makes the
/// comparison hold; a cell measured an hour earlier would carry every other
/// difference of the host with it.
/// </summary>
internal static class InterferenceScenario
{
    private const string PurgeSql = """
        DELETE FROM platform.processed_messages
        WHERE processed_at < @threshold
        """;

    internal static async Task<InterferenceResult> RunAsync(
        ProbeDatabase database,
        ContentionArm mixture,
        int volume,
        int marks,
        TimeSpan retention,
        TimeSpan duration,
        int maxTransactions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        await BacklogSeeder.FillProcessedMessagesAsync(database, marks, retention, cancellationToken);
        await database.ExecuteAsync("CHECKPOINT", cancellationToken);

        ArmResult quiet = await ArmRunner.RunAsync(
            database.DataSource, mixture, volume, duration, maxTransactions, cancellationToken);

        var purgeSeconds = 0.0;
        var purged = 0;
        Task purge = Task.Run(
            async () =>
            {
                var started = Stopwatch.GetTimestamp();
                await using NpgsqlConnection connection =
                    await database.DataSource.OpenConnectionAsync(cancellationToken);
                await using NpgsqlCommand command = connection.CreateCommand();
                command.CommandTimeout = 0;
                command.CommandText = PurgeSql;
                command.Parameters.AddWithValue("threshold", DateTimeOffset.UtcNow - retention);
                purged = await command.ExecuteNonQueryAsync(cancellationToken);
                purgeSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            },
            cancellationToken);

        ArmResult withPurge = await ArmRunner.RunAsync(
            database.DataSource, mixture, volume, duration, maxTransactions, cancellationToken);
        await purge;

        return new InterferenceResult(volume, marks, purgeSeconds, purged, quiet, withPurge);
    }
}
