using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.IntegrationTests.Notifications;

/// <summary>Wraps or replaces the composed claim without naming its implementation.</summary>
internal static class AttachmentClaimDecoration
{
    internal static Action<IServiceCollection> Wrap(
        Func<IAttachmentClaim, IAttachmentClaim> decorate)
        => services =>
        {
            ServiceDescriptor original = services.Last(
                descriptor => descriptor.ServiceType == typeof(IAttachmentClaim));
            services.Remove(original);
            services.AddSingleton<IAttachmentClaim>(provider => decorate(
                (IAttachmentClaim)ActivatorUtilities.CreateInstance(
                    provider, original.ImplementationType!)));
        };
}

/// <summary>
/// Observes the acceptance transaction from inside it, at the one moment the
/// claim runs.
/// <para>
/// Everything it reads about the ordering it reads on the caller's own
/// transaction, where the rows this acceptance has written so far are visible
/// and nobody else's are. Everything it reads about the locks it reads on a
/// connection of its own, because the question is which sessions hold them and
/// a session cannot see itself as one of several.
/// </para>
/// </summary>
internal sealed class AttachmentClaimProbe(IAttachmentClaim inner, string connectionString)
    : IAttachmentClaim
{
    private const string SessionSql = """
        SELECT pg_backend_pid(), current_setting('transaction_isolation')
        """;

    private const string WrittenSoFarSql = """
        SELECT
            (SELECT count(*) FROM notifications.notification WHERE id = @notificationId),
            (SELECT count(*) FROM platform.outbox WHERE payload::text LIKE @payloadPattern),
            (SELECT count(*) FROM audit.audit_event WHERE entity_id = @entityId)
        """;

    // Table-level locks are the ones a session announces. A statement that
    // locks rows of the attachment table announces a row-share lock over the
    // table, and the insert of a dependency announces a row-exclusive one, so
    // a claim running on a connection of its own would show up here as a
    // second session.
    private const string LockHoldersSql = """
        SELECT DISTINCT pid
        FROM pg_locks
        WHERE locktype = 'relation'
          AND relation IN (
              'attachmentmanagement.attachment'::regclass,
              'attachmentmanagement.attachment_dependency'::regclass)
        ORDER BY pid
        """;

    /// <summary>The isolation level the server reports for the acceptance transaction.</summary>
    internal string? Isolation { get; private set; }

    /// <summary>The backend the acceptance transaction runs on.</summary>
    internal int AcceptancePid { get; private set; }

    internal int NotificationRowsBeforeClaim { get; private set; } = -1;

    internal int OutboxRowsBeforeClaim { get; private set; } = -1;

    internal int AuditRowsBeforeClaim { get; private set; } = -1;

    /// <summary>Every session holding a lock on the attachment tables while the claim holds its own.</summary>
    internal IReadOnlyList<int> SessionsHoldingAttachmentLocks { get; private set; } = [];

    public async Task<AttachmentClaimOutcome> ClaimAsync(
        DbTransaction transaction,
        AttachmentClaimRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        await ReadSessionAsync(transaction, cancellationToken);
        await ReadWrittenSoFarAsync(transaction, request.NotificationId, cancellationToken);

        AttachmentClaimOutcome outcome = await inner.ClaimAsync(
            transaction, request, cancellationToken);

        SessionsHoldingAttachmentLocks = await ReadLockHoldersAsync(cancellationToken);
        return outcome;
    }

    private async Task ReadSessionAsync(
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CommandOn(transaction, SessionSql);
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        AcceptancePid = reader.GetInt32(0);
        Isolation = reader.GetString(1);
    }

    private async Task ReadWrittenSoFarAsync(
        DbTransaction transaction,
        Guid notificationId,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = CommandOn(transaction, WrittenSoFarSql);
        AddParameter(command, "notificationId", notificationId);
        AddParameter(command, "payloadPattern", $"%{notificationId}%");
        AddParameter(command, "entityId", notificationId.ToString());
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        NotificationRowsBeforeClaim = (int)reader.GetInt64(0);
        OutboxRowsBeforeClaim = (int)reader.GetInt64(1);
        AuditRowsBeforeClaim = (int)reader.GetInt64(2);
    }

    private async Task<IReadOnlyList<int>> ReadLockHoldersAsync(CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = LockHoldersSql;
        var holders = new List<int>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            holders.Add(reader.GetInt32(0));
        }

        return holders;
    }

    private static DbCommand CommandOn(DbTransaction transaction, string sql)
    {
        DbConnection connection = transaction.Connection
            ?? throw new InvalidOperationException("A sonda exige uma transação com conexão aberta.");
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}

/// <summary>A claim that never reaches the store: the failure point before anything is written.</summary>
internal sealed class ThrowingAttachmentClaim : IAttachmentClaim
{
    internal const string Message = "Falha induzida no claim de anexos.";

    public Task<AttachmentClaimOutcome> ClaimAsync(
        DbTransaction transaction,
        AttachmentClaimRequest request,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);
}

/// <summary>
/// A claim that writes the set and then fails: the failure point between a
/// durable claim and everything the acceptance would have written after it.
/// </summary>
internal sealed class ClaimThenFail(IAttachmentClaim inner) : IAttachmentClaim
{
    internal const string Message = "Falha induzida depois do claim de anexos.";

    public async Task<AttachmentClaimOutcome> ClaimAsync(
        DbTransaction transaction,
        AttachmentClaimRequest request,
        CancellationToken cancellationToken)
    {
        await inner.ClaimAsync(transaction, request, cancellationToken);
        throw new InvalidOperationException(Message);
    }
}

/// <summary>
/// A claim that writes the set and then lets someone else win the idempotency
/// race, on a connection of its own and with a commit of its own.
/// <para>
/// It is what makes the losing path deterministic. The registration it commits
/// is the one the acceptance is about to insert, so the insert violates the
/// unique key exactly as a concurrent producer would have made it violate, and
/// the losing unit reaches the read of the winner with its claim written and
/// every row lock of the set held.
/// </para>
/// </summary>
internal sealed class ClaimThenLoseTheRace(
    IAttachmentClaim inner,
    string connectionString,
    string application,
    string idempotencyKey,
    string payloadHash,
    Guid winnerNotificationId) : IAttachmentClaim
{
    private const string InsertRegistrationSql = """
        INSERT INTO notifications.idempotency_key
            (application, idempotency_key, payload_hash, notification_id, created_at)
        VALUES (@application, @idempotencyKey, @payloadHash, @notificationId, now())
        """;

    public async Task<AttachmentClaimOutcome> ClaimAsync(
        DbTransaction transaction,
        AttachmentClaimRequest request,
        CancellationToken cancellationToken)
    {
        AttachmentClaimOutcome outcome = await inner.ClaimAsync(
            transaction, request, cancellationToken);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using NpgsqlCommand registration = connection.CreateCommand();
        registration.CommandText = InsertRegistrationSql;
        registration.Parameters.AddWithValue("application", application);
        registration.Parameters.AddWithValue("idempotencyKey", idempotencyKey);
        registration.Parameters.AddWithValue("payloadHash", payloadHash);
        registration.Parameters.AddWithValue("notificationId", winnerNotificationId);
        await registration.ExecuteNonQueryAsync(cancellationToken);
        return outcome;
    }
}

/// <summary>An audit trail that appends and then fails: the last failure point before the commit.</summary>
internal sealed class AppendThenFailAuditTrail(IAuditTrail inner) : IAuditTrail
{
    internal const string Message = "Falha induzida depois do append da trilha de auditoria.";

    public async Task AppendAsync(
        DbTransaction transaction,
        AuditEntry entry,
        CancellationToken cancellationToken)
    {
        await inner.AppendAsync(transaction, entry, cancellationToken);
        throw new InvalidOperationException(Message);
    }

    public Task RecordApprovalAsync(
        DbTransaction transaction,
        ApprovalGrant grant,
        CancellationToken cancellationToken)
        => inner.RecordApprovalAsync(transaction, grant, cancellationToken);
}

/// <summary>An outbox writer that never appends: the failure point after the notification exists.</summary>
internal sealed class FailingOutboxWriter : NotificationHub.Api.Infrastructure.Messaging.IOutboxWriter
{
    internal const string Message = "Falha induzida no append da outbox.";

    public Task<Guid> AppendAsync(
        DbTransaction transaction,
        NotificationHub.Api.Infrastructure.Messaging.OutboxAppend message,
        CancellationToken cancellationToken)
        => throw new InvalidOperationException(Message);
}
