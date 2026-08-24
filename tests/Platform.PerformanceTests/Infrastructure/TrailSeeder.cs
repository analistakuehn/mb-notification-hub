using System.Globalization;
using Npgsql;
using NpgsqlTypes;

namespace NotificationHub.PerformanceTests.Infrastructure;

/// <summary>
/// Fills a monthly partition up to a target row count with a chain that
/// actually links: the seeded rows continue from whatever tail the partition
/// already has, and every row carries its own canonical text and hashes. A
/// partition seeded with unrelated hashes would still cost the same to scan,
/// but it could not be replayed, and the verification scenario would have
/// nothing to measure.
/// </summary>
internal static class TrailSeeder
{
    private const string CopySql = """
        COPY audit.audit_event
            (id, seq, occurred_at, actor_type, actor_id, application, action,
             entity_type, entity_id, details, canonical, prev_hash, hash)
        FROM STDIN (FORMAT BINARY)
        """;

    private const string DetailsJson =
        """{"channel":"push","class":"critical","origin":"seed","template":"otp-login","version":7}""";

    internal static async Task<int> CountAsync(
        ProbeDatabase database,
        PartitionMonth month,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(month);
        return (int)await database.ScalarAsync<long>(
            $"""SELECT count(*) FROM audit."{month.Name}" """, cancellationToken);
    }

    /// <summary>Grows the partition to <paramref name="target"/> rows and returns how many it added.</summary>
    internal static async Task<int> EnsureRowsAsync(
        ProbeDatabase database,
        PartitionMonth month,
        int target,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(month);
        var present = await CountAsync(database, month, cancellationToken);
        var missing = target - present;
        if (missing <= 0)
        {
            return 0;
        }

        var running = await TailHashAsync(database, month, cancellationToken)
            ?? AuditChainArithmetic.PartitionAnchor(month.Name);
        var first = await ReserveSequenceAsync(database, missing, cancellationToken);
        TimeSpan span = month.ToExclusive - month.FromInclusive;

        // Step first, multiply second. Multiplying the month's ticks by the row
        // index overflows a long past a few hundred thousand rows, and the row
        // then lands outside every partition instead of inside this one.
        var step = (span.Ticks - TimeSpan.TicksPerMinute) / Math.Max(target, 1);

        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using (NpgsqlBinaryImporter writer =
            await connection.BeginBinaryImportAsync(CopySql, cancellationToken))
        {
            for (var index = 0; index < missing; index++)
            {
                var seq = first + index;
                DateTimeOffset occurredAt = month.FromInclusive + TimeSpan.FromTicks(step * (index + present));
                var entry = new TrailEntry(
                    Guid.CreateVersion7(),
                    occurredAt,
                    "system",
                    "trail-seeder",
                    "araia-cambio-api",
                    "notification.accepted",
                    "notification",
                    string.Create(CultureInfo.InvariantCulture, $"seed-{seq}"),
                    DetailsJson);
                var canonical = AuditChainArithmetic.CanonicalDocument(entry, seq);
                var hash = AuditChainArithmetic.Link(running, canonical);

                // Synchronous writes on purpose: each value is a memory copy
                // into the importer's buffer, and thirteen await points per row
                // cost more than the copy at ten million rows.
                writer.StartRow();
                writer.Write(entry.Id, NpgsqlDbType.Uuid);
                writer.Write(seq, NpgsqlDbType.Bigint);
                writer.Write(AuditChainArithmetic.TruncateToMicroseconds(occurredAt), NpgsqlDbType.TimestampTz);
                writer.Write(entry.ActorType, NpgsqlDbType.Varchar);
                writer.Write(entry.ActorId, NpgsqlDbType.Varchar);
                writer.Write(entry.Application!, NpgsqlDbType.Varchar);
                writer.Write(entry.Action, NpgsqlDbType.Varchar);
                writer.Write(entry.EntityType, NpgsqlDbType.Varchar);
                writer.Write(entry.EntityId, NpgsqlDbType.Varchar);
                writer.Write(entry.DetailsJson, NpgsqlDbType.Jsonb);
                writer.Write(canonical, NpgsqlDbType.Text);
                writer.Write(running, NpgsqlDbType.Bytea);
                writer.Write(hash, NpgsqlDbType.Bytea);

                running = hash;
                if (progress is not null && (index + 1) % 250_000 == 0)
                {
                    progress.Report(
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"    carga da partição {month.Name}: {index + 1:N0} de {missing:N0} linhas"));
                }
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await database.ExecuteAsync($"""ANALYZE audit."{month.Name}" """, cancellationToken);
        return missing;
    }

    internal static async Task<byte[]?> TailHashAsync(
        ProbeDatabase database,
        PartitionMonth month,
        CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection =
            await database.DataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandTimeout = 0;
        command.CommandText = """
            SELECT hash
            FROM audit.audit_event
            WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive AND hash IS NOT NULL
            ORDER BY seq DESC
            LIMIT 1
            """;
        command.Parameters.AddWithValue("fromInclusive", month.FromInclusive);
        command.Parameters.AddWithValue("toExclusive", month.ToExclusive);
        return await command.ExecuteScalarAsync(cancellationToken) as byte[];
    }

    /// <summary>
    /// Takes a contiguous block of sequence values out of the shared sequence,
    /// so the seeded rows never collide with what an arm appends afterwards.
    /// </summary>
    private static async Task<long> ReserveSequenceAsync(
        ProbeDatabase database,
        int count,
        CancellationToken cancellationToken)
    {
        var first = await database.ScalarAsync<long>(
            "SELECT nextval(pg_get_serial_sequence('audit.audit_event', 'seq'))", cancellationToken);
        await database.ScalarAsync<long>(
            string.Create(
                CultureInfo.InvariantCulture,
                $"SELECT setval(pg_get_serial_sequence('audit.audit_event', 'seq'), {first + count - 1})"),
            cancellationToken);
        return first;
    }
}
