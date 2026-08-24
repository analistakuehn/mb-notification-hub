using System.Diagnostics;
using System.Globalization;
using Npgsql;
using NotificationHub.PerformanceTests.Infrastructure;

namespace NotificationHub.PerformanceTests.Contention;

/// <summary>Which append shape the arm exercises.</summary>
internal enum AppendShape
{
    /// <summary>
    /// The shape the appender had before the collapse: four round trips inside
    /// the lock window (lock, nextval, previous hash, insert) plus the commit.
    /// It stays in the design as the arm that shows what the correction bought.
    /// </summary>
    Current,

    /// <summary>
    /// What the appender does today: lock and sequence value folded into one
    /// statement, so the window holds three round trips plus the commit. The
    /// previous hash stays in a statement of its own because it cannot be read
    /// in the statement that waits for the lock.
    /// </summary>
    Collapsed,
}

/// <summary>
/// One append, split into the phases that decide the ceiling of a partition.
/// Everything is in milliseconds.
/// </summary>
/// <param name="SetupMs">Connection, transaction start and the business statements that precede the append.</param>
/// <param name="WaitMs">Time spent waiting for the chain advisory lock.</param>
/// <param name="PreCommitMs">Work done while the lock is already held, before the commit.</param>
/// <param name="CommitMs">The commit itself, which is when the lock is finally released.</param>
internal sealed record AppendSample(double SetupMs, double WaitMs, double PreCommitMs, double CommitMs)
{
    /// <summary>The window during which every other appender of the partition is blocked.</summary>
    internal double HoldMs => PreCommitMs + CommitMs;

    /// <summary>What the caller of the append experiences.</summary>
    internal double LatencyMs => SetupMs + WaitMs + HoldMs;
}

/// <summary>What the chain read brought back, and how long the lock made it wait.</summary>
internal sealed record ChainRead(long Seq, byte[]? PrevHash, double ServerWaitMs);

/// <summary>One operation of the real append profile, as the writers of the repository shape it.</summary>
/// <param name="Name">Operation name, for the report.</param>
/// <param name="Weight">Share of the mixture.</param>
/// <param name="BusinessStatements">Statements that run before the append, outside the lock window.</param>
/// <param name="AppendsPerTransaction">How many trail rows the same transaction appends.</param>
internal sealed record AppendOperation(string Name, int Weight, int BusinessStatements, int AppendsPerTransaction);

/// <summary>
/// Runs one append against the real schema with the real statements, timing
/// each phase separately.
/// </summary>
/// <remarks>
/// The probe issues the statements itself instead of calling the production
/// appender for two reasons that both point the same way: the production type
/// is internal to its assembly, and no timing seam exists inside it that could
/// separate waiting from holding. The SQL below is the appender's SQL, kept
/// literal on purpose, so a change there that this file does not follow shows
/// up as a measurement that stops matching production.
/// </remarks>
internal sealed class AuditAppender(NpgsqlDataSource dataSource, AppendShape shape)
{
    /// <summary>
    /// Generous on purpose. At the volumes where the partition ceiling
    /// collapses, an appender waits minutes behind the queue, and the default
    /// thirty seconds would turn the measurement into an exception instead of
    /// a number. It is still bounded, so a pathological cell ends.
    /// </summary>
    private const int CommandTimeoutSeconds = 600;

    private const string AcquireChainLockSql = "SELECT pg_advisory_xact_lock(@lockKey)";

    private const string NextSequenceValueSql =
        "SELECT nextval(pg_get_serial_sequence('audit.audit_event', 'seq'))";

    private const string LastChainedHashSql = """
        SELECT hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive AND hash IS NOT NULL
        ORDER BY seq DESC
        LIMIT 1
        """;

    // Lock and sequence value in one round trip, and the tail read left in a
    // statement of its own. Folding the tail read in as well was measured and
    // rejected: it forked the chain on 6,707 of 8,711 links. The reason is the
    // isolation level, not the planner. Under READ COMMITTED a statement takes
    // its snapshot when it starts, which is before it blocks on the lock, so a
    // statement that waits and then reads the trail reads a snapshot older than
    // the commit of the appender it waited for. The tail comes back stale and
    // the next link points at the wrong predecessor. Only a statement that
    // starts after the lock statement returned sees the predecessor's row.
    //
    // What is left is honest: four round trips inside the window become three.
    // nextval sits in the projection over the locked CTE, which is evaluated
    // per output row and therefore only after the lock was granted, keeping
    // sequence order equal to chain order. The isolation level rides in the
    // same projection, exactly as the appender reads it, because the fold is
    // only correct under READ COMMITTED. The granted instant comes back with
    // both so the caller can split waiting from holding without paying another
    // round trip for the clock; that term is the probe's own and the appender
    // has no use for it.
    private const string LockAndNextSequenceSql = """
        WITH chain_lock AS MATERIALIZED (
            SELECT pg_advisory_xact_lock(@lockKey) AS taken
        ), granted AS (
            SELECT clock_timestamp() AS at FROM chain_lock
        )
        SELECT
            EXTRACT(EPOCH FROM (granted.at - statement_timestamp())) * 1000,
            current_setting('transaction_isolation'),
            nextval(pg_get_serial_sequence('audit.audit_event', 'seq'))
        FROM granted
        """;

    private const string InsertAuditEventSql = """
        INSERT INTO audit.audit_event
            (id, seq, occurred_at, actor_type, actor_id, application, action,
             entity_type, entity_id, details, canonical, prev_hash, hash)
        VALUES
            (@id, @seq, @occurredAt, @actorType, @actorId, @application, @action,
             @entityType, @entityId, CAST(@details AS jsonb), @canonical, @prevHash, @hash)
        """;

    // Stands in for the business statements of the producing module: what
    // matters to the ceiling is that they run before the lock, not which table
    // they touch. The outbox is the one table every writer of the repository
    // does touch on this path.
    private const string BusinessStatementSql = """
        INSERT INTO platform.outbox
            (id, destination, event_type, message_key, headers, payload, priority_class, created_at, transport)
        VALUES
            (@id, 'core-critical', 'probe.business', @key, '{}'::jsonb, CAST(@payload AS jsonb),
             'critical', @createdAt, 'sqs')
        """;

    internal async Task<AppendSample> AppendAsync(
        PartitionMonth month,
        AppendOperation operation,
        int salt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(month);
        ArgumentNullException.ThrowIfNull(operation);

        var start = Stopwatch.GetTimestamp();
        await using NpgsqlConnection connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlTransaction transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        for (var statement = 0; statement < operation.BusinessStatements; statement++)
        {
            await RunBusinessStatementAsync(connection, transaction, salt, statement, cancellationToken);
        }

        var afterSetup = Stopwatch.GetTimestamp();
        var waitMs = 0.0;
        var preCommitMs = 0.0;

        for (var append = 0; append < operation.AppendsPerTransaction; append++)
        {
            var beforeLock = Stopwatch.GetTimestamp();
            ChainRead read = await LockAndReadAsync(connection, transaction, month, cancellationToken);
            var afterRead = Stopwatch.GetTimestamp();

            TrailEntry entry = BuildEntry(month, salt, append);
            var canonical = AuditChainArithmetic.CanonicalDocument(entry, read.Seq);
            var previous = read.PrevHash ?? AuditChainArithmetic.PartitionAnchor(month.Name);
            var hash = AuditChainArithmetic.Link(previous, canonical);
            await InsertAsync(
                connection, transaction, entry, read.Seq, canonical, previous, hash, cancellationToken);
            var afterInsert = Stopwatch.GetTimestamp();

            // Both shapes report how long they waited: the current shape times
            // its own lock statement, the folded one brings the granted instant
            // back from the server. What is left of the statement is work done
            // with the lock already held, and it belongs to the hold window.
            var statementMs = Stopwatch.GetElapsedTime(beforeLock, afterRead).TotalMilliseconds;
            var measuredWait = Math.Clamp(read.ServerWaitMs, 0, statementMs);
            waitMs += measuredWait;
            preCommitMs += statementMs - measuredWait
                + Stopwatch.GetElapsedTime(afterRead, afterInsert).TotalMilliseconds;
        }

        var beforeCommit = Stopwatch.GetTimestamp();
        await transaction.CommitAsync(cancellationToken);
        var end = Stopwatch.GetTimestamp();

        return new AppendSample(
            Stopwatch.GetElapsedTime(start, afterSetup).TotalMilliseconds,
            waitMs,
            preCommitMs,
            Stopwatch.GetElapsedTime(beforeCommit, end).TotalMilliseconds);
    }

    private static TrailEntry BuildEntry(PartitionMonth month, int salt, int append)
        => new(
            Guid.CreateVersion7(),
            month.InstantAt((salt * 7919L) + append),
            "system",
            "contention-probe",
            "araia-cambio-api",
            "notification.accepted",
            "notification",
            Guid.CreateVersion7().ToString("D"),
            """{"channel":"push","class":"critical","origin":"contention-probe","template":"otp-login","version":7}""");

    private static async Task RunBusinessStatementAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int salt,
        int statement,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = BusinessStatementSql;
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue(
            "key", string.Create(CultureInfo.InvariantCulture, $"probe-{salt}-{statement}"));
        command.Parameters.AddWithValue(
            "payload", """{"notificationId":"00000000-0000-0000-0000-000000000000"}""");
        command.Parameters.AddWithValue("createdAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<ChainRead> LockAndReadAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        PartitionMonth month,
        CancellationToken cancellationToken)
    {
        if (shape is AppendShape.Collapsed)
        {
            double foldedWaitMs;
            long foldedSeq;
            await using (NpgsqlCommand folded = connection.CreateCommand())
            {
                folded.Transaction = transaction;
                folded.CommandTimeout = CommandTimeoutSeconds;
                folded.CommandText = LockAndNextSequenceSql;
                folded.Parameters.AddWithValue("lockKey", month.LockKey);
                await using NpgsqlDataReader reader = await folded.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                foldedWaitMs = reader.GetDouble(0);

                // The appender refuses anything but READ COMMITTED, and a probe
                // measuring a database that runs under another level would be
                // measuring a shape production never accepts.
                var isolation = reader.GetString(1);
                if (!string.Equals(isolation, "read committed", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"A transação está em '{isolation}' e a cadeia exige READ COMMITTED.");
                }

                foldedSeq = reader.GetInt64(2);
            }

            await using NpgsqlCommand foldedTail = connection.CreateCommand();
            foldedTail.Transaction = transaction;
            foldedTail.CommandTimeout = CommandTimeoutSeconds;
            foldedTail.CommandText = LastChainedHashSql;
            foldedTail.Parameters.AddWithValue("fromInclusive", month.FromInclusive);
            foldedTail.Parameters.AddWithValue("toExclusive", month.ToExclusive);
            var foldedPrev = await foldedTail.ExecuteScalarAsync(cancellationToken);
            return new ChainRead(foldedSeq, foldedPrev as byte[], foldedWaitMs);
        }

        var lockStart = Stopwatch.GetTimestamp();
        await using (NpgsqlCommand chainLock = connection.CreateCommand())
        {
            chainLock.Transaction = transaction;
            chainLock.CommandTimeout = CommandTimeoutSeconds;
            chainLock.CommandText = AcquireChainLockSql;
            chainLock.Parameters.AddWithValue("lockKey", month.LockKey);
            await chainLock.ExecuteNonQueryAsync(cancellationToken);
        }

        var lockWaitMs = Stopwatch.GetElapsedTime(lockStart, Stopwatch.GetTimestamp()).TotalMilliseconds;

        long sequenceValue;
        await using (NpgsqlCommand sequence = connection.CreateCommand())
        {
            sequence.Transaction = transaction;
            sequence.CommandTimeout = CommandTimeoutSeconds;
            sequence.CommandText = NextSequenceValueSql;
            sequenceValue = (long)(await sequence.ExecuteScalarAsync(cancellationToken))!;
        }

        await using NpgsqlCommand last = connection.CreateCommand();
        last.Transaction = transaction;
        last.CommandTimeout = CommandTimeoutSeconds;
        last.CommandText = LastChainedHashSql;
        last.Parameters.AddWithValue("fromInclusive", month.FromInclusive);
        last.Parameters.AddWithValue("toExclusive", month.ToExclusive);
        var value = await last.ExecuteScalarAsync(cancellationToken);
        return new ChainRead(sequenceValue, value as byte[], lockWaitMs);
    }

    private static async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        TrailEntry entry,
        long seq,
        string canonical,
        byte[] prevHash,
        byte[] hash,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = InsertAuditEventSql;
        command.Parameters.AddWithValue("id", entry.Id);
        command.Parameters.AddWithValue("seq", seq);
        command.Parameters.AddWithValue("occurredAt", entry.OccurredAt);
        command.Parameters.AddWithValue("actorType", entry.ActorType);
        command.Parameters.AddWithValue("actorId", entry.ActorId);
        command.Parameters.AddWithValue("application", entry.Application ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("action", entry.Action);
        command.Parameters.AddWithValue("entityType", entry.EntityType);
        command.Parameters.AddWithValue("entityId", entry.EntityId);
        command.Parameters.AddWithValue("details", entry.DetailsJson);
        command.Parameters.AddWithValue("canonical", canonical);
        command.Parameters.AddWithValue("prevHash", prevHash);
        command.Parameters.AddWithValue("hash", hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
