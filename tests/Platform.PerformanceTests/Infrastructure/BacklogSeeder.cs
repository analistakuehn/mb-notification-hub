using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace NotificationHub.PerformanceTests.Infrastructure;

/// <summary>
/// Synthetic backlogs for the two platform tables whose cost only shows up in
/// volume: the outbox the relay claims from, and the dedupe marks the purge
/// walks. Both are filled with the shape the producers write, so the planner
/// sees the selectivity it will see in production.
/// </summary>
internal static class BacklogSeeder
{
    private const string OutboxCopySql = """
        COPY platform.outbox
            (id, destination, event_type, message_key, headers, payload,
             priority_class, created_at, sent_at, transport)
        FROM STDIN (FORMAT BINARY)
        """;

    private const string ProcessedCopySql = """
        COPY platform.processed_messages (message_id, consumer, processed_at)
        FROM STDIN (FORMAT BINARY)
        """;

    private const string HeadersJson = """{"traceparent":"00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01"}""";

    /// <summary>
    /// Pending rows across the four bands, with the auth band deliberately
    /// rare. The relay claims one band at a time, and a rare band is what makes
    /// the cost of the band expression visible: the scan has to walk past the
    /// rows of every other band to fill one batch.
    /// </summary>
    internal static async Task<int> FillOutboxAsync(
        ProbeDatabase database,
        int pending,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var present = (int)await database.ScalarAsync<long>(
            "SELECT count(*) FROM platform.outbox WHERE sent_at IS NULL", cancellationToken);
        var missing = pending - present;
        if (missing <= 0)
        {
            return 0;
        }

        DateTimeOffset baseInstant = DateTimeOffset.UtcNow.AddHours(-6);
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using (NpgsqlBinaryImporter writer =
            await connection.BeginBinaryImportAsync(OutboxCopySql, cancellationToken))
        {
            for (var index = 0; index < missing; index++)
            {
                (var destination, var priority) = Route(index);
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(Guid.CreateVersion7(), NpgsqlDbType.Uuid, cancellationToken);
                await writer.WriteAsync(destination, NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync("araia.notification.requested.v1", NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(
                    string.Create(CultureInfo.InvariantCulture, $"recipient-{index}"),
                    NpgsqlDbType.Varchar,
                    cancellationToken);
                await writer.WriteAsync(HeadersJson, NpgsqlDbType.Jsonb, cancellationToken);
                await writer.WriteAsync(
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $$"""{"notificationId":"{{Guid.CreateVersion7():D}}"}"""),
                    NpgsqlDbType.Jsonb,
                    cancellationToken);
                await writer.WriteAsync(priority, NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(
                    baseInstant.AddMilliseconds(index), NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteNullAsync(cancellationToken);
                await writer.WriteAsync("sqs", NpgsqlDbType.Varchar, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await database.ExecuteAsync("ANALYZE platform.outbox", cancellationToken);
        return missing;
    }

    /// <summary>Dedupe marks older than the retention window, which is what a purge round removes.</summary>
    internal static async Task<int> FillProcessedMessagesAsync(
        ProbeDatabase database,
        int marks,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        var present = (int)await database.ScalarAsync<long>(
            "SELECT count(*) FROM platform.processed_messages", cancellationToken);
        var missing = marks - present;
        if (missing <= 0)
        {
            return 0;
        }

        DateTimeOffset oldest = DateTimeOffset.UtcNow - retention - TimeSpan.FromDays(1);
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using (NpgsqlBinaryImporter writer =
            await connection.BeginBinaryImportAsync(ProcessedCopySql, cancellationToken))
        {
            for (var index = 0; index < missing; index++)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(
                    string.Create(CultureInfo.InvariantCulture, $"notifications.requested.v1:0:{index + present}"),
                    NpgsqlDbType.Varchar,
                    cancellationToken);
                await writer.WriteAsync("kafka-ingress", NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(
                    oldest.AddMilliseconds(-index), NpgsqlDbType.TimestampTz, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await database.ExecuteAsync("ANALYZE platform.processed_messages", cancellationToken);
        return missing;
    }

    private static (string Destination, string Priority) Route(int index) => (index % 100) switch
    {
        < 2 => ("core-auth", "critical"),
        < 20 => ("core-critical", "critical"),
        < 80 => ("core-transactional", "transactional"),
        _ => ("core-operational", "operational"),
    };
}
