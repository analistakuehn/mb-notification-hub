using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// The module health degrades when the contiguous future partition coverage
/// of audit_event falls under the configured window. The minimum is set to 45
/// days so the outcome never depends on the day of month the test runs: with
/// the migrated coverage (current month plus two) the check reports healthy,
/// and dropping the next month's partition always leaves less than 45 days.
/// </summary>
public sealed class AuditPartitionHealthCheckTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    [RequiresDockerFact]
    public async Task Dropping_the_next_month_partition_degrades_the_module_health()
    {
        await using var factory = new HealthProbeFactory(_postgres.GetConnectionString());
        using (IServiceScope migrationScope = factory.Services.CreateScope())
        {
            // The origin module's history creates the trail tables the Audit
            // adoption migration takes over, so it must run first.
            await migrationScope.ServiceProvider
                .GetRequiredService<TemplateManagementDbContext>()
                .Database.MigrateAsync();
            await migrationScope.ServiceProvider
                .GetRequiredService<AuditDbContext>()
                .Database.MigrateAsync();

            // The notifications coverage check probes the same database, so
            // its partitioned parent must exist for the healthy baseline.
            await migrationScope.ServiceProvider
                .GetRequiredService<NotificationsDbContext>()
                .Database.MigrateAsync();
        }

        HttpClient client = factory.CreateClient();
        HttpResponseMessage healthy = await client.GetAsync("/health");
        healthy.IsSuccessStatusCode.ShouldBeTrue();
        (await healthy.Content.ReadAsStringAsync()).ShouldBe("Healthy");

        DateTime now = DateTime.UtcNow;
        DateOnly nextMonth = new DateOnly(now.Year, now.Month, 1).AddMonths(1);
        var partition = $"audit_event_{nextMonth.Year:D4}_{nextMonth.Month:D2}";
        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AuditDbContext db = scope.ServiceProvider.GetRequiredService<AuditDbContext>();
            var dropSql = $"DROP TABLE IF EXISTS audit.\"{partition}\"";
            await db.Database.ExecuteSqlRawAsync(dropSql);
        }

        HttpResponseMessage degraded = await client.GetAsync("/health");
        (await degraded.Content.ReadAsStringAsync()).ShouldBe("Degraded");
    }

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (DockerEnvironment.IsAvailable)
        {
            await _postgres.StartAsync();
        }
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    private sealed class HealthProbeFactory(string connectionString) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.ConfigureAppConfiguration((_, configuration)
                => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Modules:Audit:Persistence:Ef:ConnectionString"] = connectionString,
                    ["Modules:Notifications:Persistence:Ef:ConnectionString"] = connectionString,
                    ["Modules:TemplateManagement:Persistence:Ef:ConnectionString"] = connectionString,

                    // The manager would recreate the dropped partition and the
                    // check must not race it.
                    ["Modules:Audit:PartitionManager:Enabled"] = "false",
                    ["Modules:Audit:PartitionManager:FutureWindowMinimumDays"] = "45",
                    ["Modules:Notifications:PartitionManager:Enabled"] = "false",
                }));
    }
}
