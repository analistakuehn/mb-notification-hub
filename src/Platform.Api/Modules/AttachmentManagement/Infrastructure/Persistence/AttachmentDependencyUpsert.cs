using System.Data.Common;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// The one statement that takes a dependency, written once and run by every
/// dependent this module has.
/// <para>
/// It takes the transaction rather than a context because its two callers do
/// not share one. The registry opens a transaction of its own and hands it
/// down; the claim is handed a transaction someone else opened, over a
/// connection someone else owns, and it may not open a second one. A statement
/// that reached the database through a context would be usable by the first
/// caller and unusable by the second, and the text would have to exist twice
/// for two writes that must never drift apart.
/// </para>
/// <para>
/// The write revives a hold that was already released and touches nothing
/// else, so the number of rows it changed is what separates a hold the caller
/// created from one it found. A live hold is never revised: its reason and its
/// instant describe the acquisition that is still running, and letting a later
/// call overwrite them would erase when the protection started and why.
/// </para>
/// </summary>
internal static class AttachmentDependencyUpsert
{
    private const string Sql = """
        INSERT INTO attachmentmanagement.attachment_dependency
            (id, attachment_id, reason, holder, acquired_at, released_at, version)
        VALUES
            (@id, @attachmentId, @reason, @holder, @acquiredAt, NULL, 1)
        ON CONFLICT (attachment_id, holder) DO UPDATE
        SET reason = EXCLUDED.reason,
            acquired_at = EXCLUDED.acquired_at,
            released_at = NULL,
            version = attachment_dependency.version + 1
        WHERE attachment_dependency.released_at IS NOT NULL
        """;

    /// <summary>
    /// Takes one hold inside <paramref name="transaction"/> and answers how
    /// many rows it wrote: one when the hold is this call's, zero when a live
    /// hold of the same dependent was already there.
    /// </summary>
    internal static async Task<int> ExecuteAsync(
        DbTransaction transaction,
        Guid attachmentId,
        string reason,
        string holder,
        DateTimeOffset acquiredAt,
        CancellationToken cancellationToken)
    {
        DbConnection connection = AttachmentTransaction.ConnectionOf(transaction);
        await using DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = Sql;
        AttachmentTransaction.AddParameter(command, "id", Guid.CreateVersion7());
        AttachmentTransaction.AddParameter(command, "attachmentId", attachmentId);
        AttachmentTransaction.AddParameter(command, "reason", reason);
        AttachmentTransaction.AddParameter(command, "holder", holder);
        AttachmentTransaction.AddParameter(command, "acquiredAt", acquiredAt);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>
/// What every statement this module runs on a transaction it did not open
/// needs before it can run: the live connection under that transaction, and
/// parameters bound to it.
/// </summary>
internal static class AttachmentTransaction
{
    internal static DbConnection ConnectionOf(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        return transaction.Connection
            ?? throw new InvalidOperationException(
                "The transaction has no open connection; this module writes on the caller's "
                    + "connection and never opens one of its own.");
    }

    internal static void AddParameter(DbCommand command, string name, object? value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
