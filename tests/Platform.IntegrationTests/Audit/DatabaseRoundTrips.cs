using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Npgsql;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Counts the statements a code path sends to the database over a transaction
/// it was handed. The trail's serialization window is measured in round trips,
/// not in wall clock, so the count is the property a test can pin: the wall
/// clock of a laptop says nothing, and folding two statements back apart would
/// leave every behavioral assertion green.
/// </summary>
internal sealed class DatabaseRoundTrips
{
    private int _executions;

    /// <summary>Statements executed since this counter was created.</summary>
    internal int Count => Volatile.Read(ref _executions);

    /// <summary>
    /// Opens a connection and a transaction whose commands are counted. The
    /// isolation level travels to the real transaction untouched, so a caller
    /// can measure the shape of the append under any level the driver accepts.
    /// </summary>
    internal async Task<CountedTransaction> BeginAsync(
        string connectionString,
        IsolationLevel isolationLevel = IsolationLevel.Unspecified,
        CancellationToken cancellationToken = default)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        NpgsqlTransaction transaction = isolationLevel is IsolationLevel.Unspecified
            ? await connection.BeginTransactionAsync(cancellationToken)
            : await connection.BeginTransactionAsync(isolationLevel, cancellationToken);
        return new CountedTransaction(transaction, this);
    }

    internal void Record() => Interlocked.Increment(ref _executions);
}

/// <summary>
/// A live PostgreSQL transaction that reports itself as a plain
/// <see cref="DbTransaction"/> and counts every statement executed through the
/// connection it exposes.
/// </summary>
internal sealed class CountedTransaction : DbTransaction
{
    private readonly NpgsqlTransaction _transaction;
    private readonly CountedConnection _connection;

    internal CountedTransaction(NpgsqlTransaction transaction, DatabaseRoundTrips counter)
    {
        _transaction = transaction;
        _connection = new CountedConnection(
            transaction.Connection
                ?? throw new InvalidOperationException("The transaction was opened without a connection."),
            counter);
    }

    public override IsolationLevel IsolationLevel => _transaction.IsolationLevel;

    protected override DbConnection DbConnection => _connection;

    internal NpgsqlTransaction Inner => _transaction;

    public override void Commit() => _transaction.Commit();

    public override Task CommitAsync(CancellationToken cancellationToken = default)
        => _transaction.CommitAsync(cancellationToken);

    public override void Rollback() => _transaction.Rollback();

    public override Task RollbackAsync(CancellationToken cancellationToken = default)
        => _transaction.RollbackAsync(cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _transaction.Dispose();
            _connection.DisposeInner();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Connection whose commands are counted; everything else delegates.</summary>
internal sealed class CountedConnection(NpgsqlConnection connection, DatabaseRoundTrips counter) : DbConnection
{
    [AllowNull]
    public override string ConnectionString
    {
        get => connection.ConnectionString;
        set => connection.ConnectionString = value;
    }

    public override string Database => connection.Database;

    public override string DataSource => connection.DataSource;

    public override string ServerVersion => connection.ServerVersion;

    public override ConnectionState State => connection.State;

    public override void ChangeDatabase(string databaseName) => connection.ChangeDatabase(databaseName);

    public override void Close() => connection.Close();

    public override void Open() => connection.Open();

    public override Task OpenAsync(CancellationToken cancellationToken) => connection.OpenAsync(cancellationToken);

    internal void DisposeInner() => connection.Dispose();

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel)
        => connection.BeginTransaction(isolationLevel);

    protected override DbCommand CreateDbCommand() => new CountedCommand(connection.CreateCommand(), this, counter);
}

/// <summary>Command that records one round trip per execution.</summary>
internal sealed class CountedCommand(
    NpgsqlCommand command,
    CountedConnection connection,
    DatabaseRoundTrips counter) : DbCommand
{
    [AllowNull]
    public override string CommandText
    {
        get => command.CommandText;
        set => command.CommandText = value;
    }

    public override int CommandTimeout
    {
        get => command.CommandTimeout;
        set => command.CommandTimeout = value;
    }

    public override CommandType CommandType
    {
        get => command.CommandType;
        set => command.CommandType = value;
    }

    public override bool DesignTimeVisible
    {
        get => command.DesignTimeVisible;
        set => command.DesignTimeVisible = value;
    }

    public override UpdateRowSource UpdatedRowSource
    {
        get => command.UpdatedRowSource;
        set => command.UpdatedRowSource = value;
    }

    protected override DbConnection? DbConnection
    {
        get => connection;
        set => throw new NotSupportedException("The counted command stays bound to the connection that created it.");
    }

    protected override DbParameterCollection DbParameterCollection => command.Parameters;

    protected override DbTransaction? DbTransaction
    {
        get => command.Transaction;
        set => command.Transaction = value switch
        {
            null => null,
            CountedTransaction counted => counted.Inner,
            _ => throw new NotSupportedException("Only a counted transaction can be assigned to a counted command."),
        };
    }

    public override void Cancel() => command.Cancel();

    public override int ExecuteNonQuery()
    {
        counter.Record();
        return command.ExecuteNonQuery();
    }

    public override Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        counter.Record();
        return command.ExecuteNonQueryAsync(cancellationToken);
    }

    public override object? ExecuteScalar()
    {
        counter.Record();
        return command.ExecuteScalar();
    }

    public override Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        counter.Record();
        return command.ExecuteScalarAsync(cancellationToken);
    }

    public override void Prepare() => command.Prepare();

    protected override DbParameter CreateDbParameter() => command.CreateParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
        counter.Record();
        return command.ExecuteReader(behavior);
    }

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
        CommandBehavior behavior,
        CancellationToken cancellationToken)
    {
        counter.Record();
        return await command.ExecuteReaderAsync(behavior, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            command.Dispose();
        }

        base.Dispose(disposing);
    }
}
