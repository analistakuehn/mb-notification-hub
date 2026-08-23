using System.Data.Common;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Writes the dedupe mark with parameterized SQL over the caller's own
/// transaction. The conflict target is the primary key, so a concurrent or
/// repeated mark resolves to zero affected rows instead of an error, which is
/// exactly the duplicate signal the consumer contract needs.
/// </summary>
internal sealed class PostgresProcessedMessageStore(TimeProvider timeProvider) : IProcessedMessageStore
{
    private const string MarkSql = """
        INSERT INTO platform.processed_messages (message_id, consumer, processed_at)
        VALUES (@messageId, @consumer, @processedAt)
        ON CONFLICT DO NOTHING
        """;

    public async Task<bool> TryMarkAsync(
        DbTransaction transaction,
        string messageId,
        string consumer,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(consumer);

        DbConnection connection = transaction.Connection
            ?? throw new InvalidOperationException(
                "The transaction has no open connection; the processed mark must join a live caller transaction.");

        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = MarkSql;
        AddParameter(command, "messageId", messageId);
        AddParameter(command, "consumer", consumer);
        AddParameter(command, "processedAt", timeProvider.GetUtcNow());
        var inserted = await command.ExecuteNonQueryAsync(cancellationToken);
        return inserted == 1;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
