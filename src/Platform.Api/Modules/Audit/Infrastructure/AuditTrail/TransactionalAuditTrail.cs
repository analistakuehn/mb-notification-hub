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
internal sealed class TransactionalAuditTrail : IAuditTrail
{
    private const string Table = "audit_event";

    private const string AcquireChainLockSql = "SELECT pg_advisory_xact_lock(@lockKey)";

    private const string NextSequenceValueSql =
        "SELECT nextval(pg_get_serial_sequence('audit.audit_event', 'seq'))";

    // The last chained event of the monthly partition. Appends serialize under
    // the advisory lock, so within a partition the sequence order of chained
    // rows is their chain order; pre-chain rows carry no hash and stay out.
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

        MonthlyPartitionWindow window = MonthlyPartitions.Plan(Table, auditEvent.OccurredAt, 0)[0];
        await AcquireChainLockAsync(connection, transaction, window, cancellationToken);
        var seq = await NextSequenceValueAsync(connection, transaction, cancellationToken);
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

    private static async Task AcquireChainLockAsync(
        DbConnection connection,
        DbTransaction transaction,
        MonthlyPartitionWindow window,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = AcquireChainLockSql;
        AddParameter(
            command,
            "lockKey",
            AuditChain.PartitionLockKey(window.FromInclusive.Year, window.FromInclusive.Month));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> NextSequenceValueAsync(
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = NextSequenceValueSql;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return (long)value!;
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
