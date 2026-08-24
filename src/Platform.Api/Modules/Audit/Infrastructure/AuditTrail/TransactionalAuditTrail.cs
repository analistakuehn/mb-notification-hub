using System.Data;
using System.Data.Common;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;

/// <summary>
/// Appends audit events and approvals with parameterized SQL over the caller's
/// own transaction, so the governed effect and its trail share one commit. The
/// chain link is computed under a partition-scoped advisory lock taken in that
/// same transaction: concurrent appenders to one monthly partition serialize,
/// the previous chained hash read after the lock is final, and the chain never
/// forks. Verification tolerance for aborted sequence values belongs to the
/// periodic verifier, not to this writer.
/// </summary>
/// <remarks>
/// <para>
/// The append holds the lock for three round trips: the lock and the sequence
/// value together, the previous hash on its own, and the insert. The previous
/// hash cannot join the first statement, and the reason is the isolation level
/// rather than the planner. A statement takes its snapshot when it starts,
/// which is before it blocks on the lock, so a statement that waits and then
/// reads the trail reads a state older than the commit of the appender it
/// waited for, links onto a stale predecessor and forks the chain. Only a
/// statement that begins after the lock statement returned sees that
/// predecessor. The lock and <c>nextval</c> do fold, because neither reads a
/// snapshot of the table and the sequence value sits in the projection over the
/// locked expression, which is evaluated after the lock is granted and
/// therefore keeps sequence order equal to chain order.
/// </para>
/// <para>
/// That shape is only correct while the caller runs in READ COMMITTED, where
/// every statement takes a fresh snapshot with the lock already held. A caller
/// in REPEATABLE READ or SERIALIZABLE takes its snapshot on the first statement
/// of the transaction, before the lock, and the stale read comes back even with
/// the statements separated. The writer therefore checks the level and refuses
/// anything else, twice: the level the caller declared, before it touches the
/// database, and the level the server reports for the running transaction,
/// which is what a server or role default can change without any caller saying
/// so.
/// </para>
/// </remarks>
internal sealed class TransactionalAuditTrail : IAuditTrail
{
    private const string Table = "audit_event";

    private const string ReadCommitted = "read committed";

    // Lock and sequence value in one round trip. The CTE is materialized on
    // purpose: it pins the order the lock and the projection are evaluated in
    // instead of leaving it to a planner decision about folding.
    private const string LockAndNextSequenceSql = """
        WITH chain_lock AS MATERIALIZED (
            SELECT pg_advisory_xact_lock(@lockKey) AS taken
        )
        SELECT
            current_setting('transaction_isolation'),
            nextval(pg_get_serial_sequence('audit.audit_event', 'seq'))
        FROM chain_lock
        """;

    // The last chained event of the monthly partition. Appends serialize under
    // the advisory lock, so within a partition the sequence order of chained
    // rows is their chain order; pre-chain rows carry no hash and stay out.
    //
    // The hash predicate is also what makes the partial tail index match:
    // dropping it from here would still return the right row and would silently
    // go back to scanning the whole partition inside the lock.
    private const string LastChainedHashSql = """
        SELECT hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive AND hash IS NOT NULL
        ORDER BY seq DESC
        LIMIT 1
        """;

    private const string InsertAuditEventSql = """
        INSERT INTO audit.audit_event
            (id, seq, occurred_at, actor_type, actor_id, application, action,
             entity_type, entity_id, details, canonical, prev_hash, hash)
        VALUES
            (@id, @seq, @occurredAt, @actorType, @actorId, @application, @action,
             @entityType, @entityId, CAST(@details AS jsonb), @canonical, @prevHash, @hash)
        """;

    private const string InsertApprovalSql = """
        INSERT INTO audit.approval
            (id, subject_type, subject_id, subject_version, content_hash, role, approver_oid, approved_at)
        VALUES
            (@id, @subjectType, @subjectId, @subjectVersion, @contentHash, @role, @approverOid, @approvedAt)
        """;

    public async Task AppendAsync(DbTransaction transaction, AuditEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        AuditEvent auditEvent = AuditEvent.Record(entry);
        DbConnection connection = OpenConnectionOf(transaction);
        RefuseStrongerDeclaredIsolation(transaction);

        MonthlyPartitionWindow window = MonthlyPartitions.Plan(Table, auditEvent.OccurredAt, 0)[0];
        var seq = await AcquireChainLockAndNextSequenceAsync(connection, transaction, window, cancellationToken);
        var prevHash = await LastChainedHashAsync(connection, transaction, window, cancellationToken)
            ?? AuditChain.PartitionAnchor(window.PartitionName);

        var canonical = AuditChain.CanonicalDocument(auditEvent.Id, seq, entry);
        var hash = AuditChain.Link(prevHash, canonical);
        await InsertAuditEventAsync(
            connection,
            transaction,
            auditEvent,
            new ChainedLink(seq, canonical, prevHash, hash),
            cancellationToken);
    }

    public async Task RecordApprovalAsync(DbTransaction transaction, ApprovalGrant grant, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(grant);
        Approval approval = Approval.Grant(grant);
        DbConnection connection = OpenConnectionOf(transaction);

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertApprovalSql;
        AddParameter(command, "id", approval.Id);
        AddParameter(command, "subjectType", approval.SubjectType);
        AddParameter(command, "subjectId", approval.SubjectId);
        AddParameter(command, "subjectVersion", approval.SubjectVersion);
        AddParameter(command, "contentHash", approval.ContentHash);
        AddParameter(command, "role", approval.Role);
        AddParameter(command, "approverOid", approval.ApproverOid);
        AddParameter(command, "approvedAt", approval.ApprovedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DbConnection OpenConnectionOf(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection
            ?? throw new InvalidOperationException(
                "The transaction has no open connection; the trail must join a live caller transaction.");
    }

    /// <summary>
    /// Refuses a transaction whose declared level gives it a snapshot older
    /// than the chain lock. It runs before any statement, so the caller that
    /// picked a stronger level on purpose fails without taking the lock.
    /// </summary>
    private static void RefuseStrongerDeclaredIsolation(DbTransaction transaction)
    {
        if (transaction.IsolationLevel is not (IsolationLevel.RepeatableRead
            or IsolationLevel.Serializable
            or IsolationLevel.Snapshot))
        {
            return;
        }

        throw new InvalidOperationException(
            $"The caller transaction is {transaction.IsolationLevel} and the trail requires READ COMMITTED: "
            + "a stronger level takes its snapshot before the chain lock is granted, so the append would "
            + "read a stale chain tail and fork the chain.");
    }

    /// <summary>
    /// Takes the chain lock of the partition and reserves the sequence value in
    /// one round trip, and brings back the isolation level the server reports
    /// for the running transaction.
    /// </summary>
    /// <remarks>
    /// The declared level is what the caller asked for; this one is what the
    /// transaction actually runs under, which a server, database or role
    /// default can set without any caller mentioning it. Reading it costs
    /// nothing here because it rides in the projection of a statement the
    /// append already pays for. Failing after the lock leaves it held until the
    /// caller's transaction ends, which is correct: an append that throws must
    /// abort the governed effect, and the trail contract never degrades.
    /// </remarks>
    private static async Task<long> AcquireChainLockAndNextSequenceAsync(
        DbConnection connection,
        DbTransaction transaction,
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LockAndNextSequenceSql;
        AddParameter(
            command,
            "lockKey",
            AuditChain.PartitionLockKey(window.FromInclusive.Year, window.FromInclusive.Month));

        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var isolation = reader.GetString(0);
        if (!string.Equals(isolation, ReadCommitted, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The server reports isolation level '{isolation}' for the caller transaction and the trail "
                + "requires READ COMMITTED: any stronger level takes its snapshot before the chain lock is "
                + "granted, so the append would read a stale chain tail and fork the chain.");
        }

        return reader.GetInt64(1);
    }

    private static async Task<byte[]?> LastChainedHashAsync(
        DbConnection connection,
        DbTransaction transaction,
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LastChainedHashSql;
        AddParameter(command, "fromInclusive", ToUtcInstant(window.FromInclusive));
        AddParameter(command, "toExclusive", ToUtcInstant(window.ToExclusive));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is byte[] hash ? hash : null;
    }

    private static async Task InsertAuditEventAsync(
        DbConnection connection,
        DbTransaction transaction,
        AuditEvent auditEvent,
        ChainedLink link,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertAuditEventSql;
        AddParameter(command, "id", auditEvent.Id);
        AddParameter(command, "seq", link.Seq);
        AddParameter(command, "occurredAt", auditEvent.OccurredAt);
        AddParameter(command, "actorType", auditEvent.ActorType);
        AddParameter(command, "actorId", auditEvent.ActorId);
        AddParameter(command, "application", auditEvent.Application, DbType.String);
        AddParameter(command, "action", auditEvent.Action);
        AddParameter(command, "entityType", auditEvent.EntityType);
        AddParameter(command, "entityId", auditEvent.EntityId);
        AddParameter(command, "details", auditEvent.DetailsJson);
        AddParameter(command, "canonical", link.Canonical);
        AddParameter(command, "prevHash", link.PrevHash);
        AddParameter(command, "hash", link.Hash);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddParameter(DbCommand command, string name, object? value, DbType? dbType = null)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        if (dbType is not null)
        {
            parameter.DbType = dbType.Value;
        }

        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ToUtcInstant(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private sealed record ChainedLink(long Seq, string Canonical, byte[] PrevHash, byte[] Hash);
}
