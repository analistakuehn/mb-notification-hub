using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

/// <summary>
/// Serializes maintenance rounds across hosts. Two instances of the
/// maintenance role must never run the closing cycle at the same time: one
/// would detach a partition the other is still exporting. The lock is
/// session-scoped and taken without waiting, so a second instance skips the
/// round instead of queueing behind it.
/// </summary>
/// <remarks>
/// The key space is deliberately distinct from the chain's: the chain lock
/// serializes appends inside a partition and is held to the end of the
/// caller's transaction, while this one spans a whole round with object-store
/// calls in it. Sharing one key space would let a maintenance round block
/// every audited effect in the system.
/// </remarks>
internal sealed class AuditMaintenanceLock(AuditDbContext db)
{
    /// <summary>High half of every maintenance lock key ("AUMT" in ASCII).</summary>
    private const long LockKeyScope = 0x41554D54;

    /// <summary>Provisioning, closing cycle and daily export.</summary>
    internal const long PartitionRoundKey = (LockKeyScope << 32) | 1;

    /// <summary>Periodic chain verification.</summary>
    internal const long VerificationRoundKey = (LockKeyScope << 32) | 2;

    /// <summary>
    /// Takes the lock without waiting. Returns null when another instance
    /// holds it, which is a normal outcome and not an error: the round is
    /// periodic and the next tick tries again.
    /// </summary>
    public async Task<IAsyncDisposable?> TryAcquireAsync(long key, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        DbConnection connection = db.Database.GetDbConnection();
        var acquired = await ExecuteLockAsync(connection, "SELECT pg_try_advisory_lock(@key)", key, cancellationToken);
        if (acquired is not true)
        {
            await db.Database.CloseConnectionAsync();
            return null;
        }

        return new Handle(db, key);
    }

    private static async Task<bool?> ExecuteLockAsync(
        DbConnection connection,
        string sql,
        long key,
        CancellationToken cancellationToken)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = sql;
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) as bool?;
    }

    private sealed class Handle(AuditDbContext db, long key) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            try
            {
                await ExecuteLockAsync(
                    db.Database.GetDbConnection(),
                    "SELECT pg_advisory_unlock(@key)",
                    key,
                    CancellationToken.None);
            }
            finally
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }
}
