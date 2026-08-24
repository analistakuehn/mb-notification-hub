using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Claims pending outbox rows with parameterized SQL over the messaging
/// context's connection. The band is read from the stored column the database
/// computes from the destination and the priority class, never spelled again
/// here: an expression in this predicate is what no index could answer. The
/// three columns of the predicate and the ordering column are the index the
/// schema declares, and the unsent filter is written literally because a
/// partial index only matches a statement that carries its predicate. The
/// stored transport is a filter, never an inference from the destination name.
/// </summary>
internal sealed class PostgresOutboxPendingStore(PlatformMessagingDbContext db) : IOutboxPendingStore
{
    /// <summary>
    /// The claim exactly as it reaches the database. It is visible to the test
    /// assemblies so the plan assertion reads this statement instead of a copy
    /// of it: a plan test over a transcribed statement grades the transcription.
    /// </summary>
    internal const string ClaimSql = """
        SELECT id, destination, event_type, message_key, headers::text, payload::text, created_at
        FROM platform.outbox
        WHERE sent_at IS NULL
          AND transport = @transport
          AND priority_band = @band
        ORDER BY created_at
        LIMIT @batchSize
        FOR UPDATE SKIP LOCKED
        """;

    public async Task<IOutboxClaim> ClaimAsync(
        OutboxBand band,
        string transport,
        int batchSize,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            DbConnection connection = db.Database.GetDbConnection();
            var messages = new List<PendingOutboxMessage>();
            await using (DbCommand command = connection.CreateCommand())
            {
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = ClaimSql;
                AddParameter(command, "band", (int)band);
                AddParameter(command, "transport", transport);
                AddParameter(command, "batchSize", batchSize);
                await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    messages.Add(new PendingOutboxMessage(
                        reader.GetGuid(0),
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetString(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetFieldValue<DateTimeOffset>(6)));
                }
            }

            return new Claim(transaction, connection, messages);
        }
        catch
        {
            await transaction.DisposeAsync();
            throw;
        }
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private sealed class Claim(
        IDbContextTransaction transaction,
        DbConnection connection,
        IReadOnlyList<PendingOutboxMessage> messages) : IOutboxClaim
    {
        private const string StampSql = "UPDATE platform.outbox SET sent_at = @sentAt WHERE id = ANY(@ids)";

        public IReadOnlyList<PendingOutboxMessage> Messages => messages;

        public async Task CompleteAsync(
            IReadOnlyCollection<Guid> sentIds,
            DateTimeOffset sentAt,
            CancellationToken cancellationToken)
        {
            if (sentIds.Count > 0)
            {
                await using DbCommand command = connection.CreateCommand();
                command.Transaction = transaction.GetDbTransaction();
                command.CommandText = StampSql;
                AddParameter(command, "sentAt", sentAt);
                AddParameter(command, "ids", sentIds.ToArray());
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        // Disposing an uncommitted claim rolls back: rows unlock, stay pending.
        public async ValueTask DisposeAsync() => await transaction.DisposeAsync();
    }
}
