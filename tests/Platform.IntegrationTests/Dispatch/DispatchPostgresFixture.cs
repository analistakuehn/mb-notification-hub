using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Dispatch;

/// <summary>
/// Disposable Postgres with the Dispatch migrations applied. The module owns
/// its own schema and history table, so the container never needs another
/// module's migrations.
/// </summary>
public sealed class DispatchPostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    public string ConnectionString => _postgres.GetConnectionString();

    public DispatchDbContext CreateDbContext()
    {
        DbContextOptions<DispatchDbContext> options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(ConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "dispatch"))
            .Options;
        return new DispatchDbContext(options);
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await _postgres.StartAsync();
        await using DispatchDbContext context = CreateDbContext();
        await context.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class DispatchPostgresCollectionDefinition : ICollectionFixture<DispatchPostgresFixture>
{
    public const string Name = "dispatch-postgres";
}
