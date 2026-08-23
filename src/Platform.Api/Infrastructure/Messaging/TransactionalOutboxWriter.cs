using System.Data.Common;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// Appends outbox messages with parameterized SQL over the caller's own
/// transaction, mirroring the transactional-append dialect of the audit trail:
/// the caller hands over the raw transaction, never a context or an entity,
/// and both sides of the effect commit together.
/// </summary>
internal sealed class TransactionalOutboxWriter(TimeProvider timeProvider) : IOutboxWriter
{
    private const string InsertSql = """
        INSERT INTO platform.outbox
            (id, destination, event_type, message_key, headers, payload, priority_class, created_at, sent_at)
        VALUES
            (@id, @destination, @eventType, @messageKey, CAST(@headers AS jsonb),
             CAST(@payload AS jsonb), @priorityClass, @createdAt, NULL)
        """;

    public async Task<Guid> AppendAsync(
        DbTransaction transaction,
        OutboxAppend message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.Destination);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.EventType);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.MessageKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.HeadersJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.PayloadJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(message.PriorityClass);

        DbConnection connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The transaction has no open connection; the outbox append must join a live caller transaction.");

        var id = Guid.CreateVersion7();
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = InsertSql;
        AddParameter(command, "id", id);
        AddParameter(command, "destination", message.Destination);
        AddParameter(command, "eventType", message.EventType);
        AddParameter(command, "messageKey", message.MessageKey);
        AddParameter(command, "headers", message.HeadersJson);
        AddParameter(command, "payload", message.PayloadJson);
        AddParameter(command, "priorityClass", message.PriorityClass);
        AddParameter(command, "createdAt", timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
