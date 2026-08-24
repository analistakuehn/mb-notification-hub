using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace NotificationHub.PerformanceTests.Infrastructure;

/// <summary>
/// The database the probe measures against: a throwaway container by default,
/// or whatever connection string the operator hands over. The schema always
/// comes from the production migrations, never from hand-written DDL, because
/// a probe running against a schema of its own would measure a table that does
/// not exist anywhere else.
/// </summary>
internal sealed class ProbeDatabase : IAsyncDisposable
{
    private readonly PostgreSqlContainer? _container;

    private ProbeDatabase(PostgreSqlContainer? container, string connectionString, NpgsqlDataSource dataSource)
    {
        _container = container;
        ConnectionString = connectionString;
        DataSource = dataSource;
    }

    internal string ConnectionString { get; }

    internal NpgsqlDataSource DataSource { get; }

    /// <summary>True when the target is a container this run owns and will drop.</summary>
    internal bool IsThrowaway => _container is not null;

    internal static async Task<ProbeDatabase> StartAsync(
        string? connectionString,
        int poolSize,
        CancellationToken cancellationToken)
    {
        PostgreSqlContainer? container = null;
        if (connectionString is null)
        {
            // Durability stays on: the commit is half of the hold window and
            // turning fsync off would measure a database nobody runs. What the
            // settings do buy is a checkpoint that does not land in the middle
            // of a twenty second arm, which on a laptop disk moves the tail by
            // an order of magnitude and would be read as contention.
            container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithCommand(
                    "-c", "max_connections=200",
                    "-c", "shared_buffers=1GB",
                    "-c", "max_wal_size=4GB",
                    "-c", "checkpoint_timeout=30min")
                .Build();
            await container.StartAsync(cancellationToken);
            connectionString = container.GetConnectionString();
        }

        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = poolSize,
            MinPoolSize = Math.Min(poolSize, 8),
        };
        NpgsqlDataSource dataSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        var database = new ProbeDatabase(container, builder.ConnectionString, dataSource);
        await database.MigrateAsync(cancellationToken);
        return database;
    }

    /// <summary>Creates the monthly partition of an instant when it does not exist yet.</summary>
    internal async Task EnsurePartitionAsync(PartitionMonth month, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(month);
        var sql = $"""
            CREATE TABLE IF NOT EXISTS audit."{month.Name}"
            PARTITION OF audit."audit_event"
            FOR VALUES FROM ('{month.FromInclusive:yyyy-MM-dd} 00:00:00+00')
            TO ('{month.ToExclusive:yyyy-MM-dd} 00:00:00+00')
            """;
        await ExecuteAsync(sql, cancellationToken);
    }

    internal async Task ExecuteAsync(string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<T?> ScalarAsync<T>(string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? default : (T)value;
    }

    internal async Task<IReadOnlyList<string>> TextsAsync(string sql, CancellationToken cancellationToken)
    {
        await using NpgsqlConnection connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0;
        var lines = new List<string>();
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lines.Add(reader.GetValue(0)?.ToString() ?? string.Empty);
        }

        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>
    /// TemplateManagement first: its history creates the trail tables that the
    /// Audit adoption migration takes over. The platform history brings the
    /// outbox and the dedupe marks the read scenarios need.
    /// </summary>
    private async Task MigrateAsync(CancellationToken cancellationToken)
    {
        DbContextOptions<TemplateManagementDbContext> templateOptions =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(ConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "templatemanagement"))
                .Options;
        await using (var templates = new TemplateManagementDbContext(templateOptions))
        {
            await templates.Database.MigrateAsync(cancellationToken);
        }

        DbContextOptions<AuditDbContext> auditOptions =
            new DbContextOptionsBuilder<AuditDbContext>()
                .UseNpgsql(ConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "audit"))
                .Options;
        await using (var audit = new AuditDbContext(auditOptions))
        {
            await audit.Database.MigrateAsync(cancellationToken);
        }

        DbContextOptions<PlatformMessagingDbContext> messagingOptions =
            new DbContextOptionsBuilder<PlatformMessagingDbContext>()
                .UseNpgsql(ConnectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"))
                .Options;
        await using var messaging = new PlatformMessagingDbContext(messagingOptions);
        await messaging.Database.MigrateAsync(cancellationToken);
    }
}
