using System.Data.Common;
using Npgsql;

namespace NotificationHub.PerformanceTests.Instrumentation;

/// <summary>How often one wait event was seen while an arm was running.</summary>
internal sealed record WaitEventTally(string WaitEventType, string WaitEvent, int Samples);

/// <summary>
/// Samples <c>pg_stat_activity</c> while an arm runs, from a connection of its
/// own. This is the third corroborating signal of the design: contention on the
/// advisory lock shows up as backends waiting on <c>Lock/advisory</c>, while a
/// saturated database or a slow commit shows up as <c>IO</c>, <c>LWLock</c> or
/// <c>IPC</c>. Reading the phase timings alone cannot tell those apart.
/// </summary>
internal sealed class WaitEventSampler(NpgsqlDataSource dataSource, TimeSpan interval)
{
    private const string SampleSql = """
        SELECT COALESCE(wait_event_type, 'Running'), COALESCE(wait_event, 'none')
        FROM pg_stat_activity
        WHERE datname = current_database()
          AND pid <> pg_backend_pid()
          AND backend_type = 'client backend'
          AND state = 'active'
        """;

    private readonly Dictionary<(string Type, string Event), int> _tallies = [];

    internal async Task<IReadOnlyList<WaitEventTally>> RunAsync(CancellationToken cancellationToken)
    {
        _tallies.Clear();
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(CancellationToken.None);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SampleAsync(connection, cancellationToken);
                await Task.Delay(interval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return
        [
            .. _tallies
                .OrderByDescending(entry => entry.Value)
                .Select(entry => new WaitEventTally(entry.Key.Type, entry.Key.Event, entry.Value)),
        ];
    }

    private async Task SampleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = SampleSql;
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            (string, string) key = (reader.GetString(0), reader.GetString(1));
            _tallies[key] = _tallies.TryGetValue(key, out var seen) ? seen + 1 : 1;
        }
    }
}
