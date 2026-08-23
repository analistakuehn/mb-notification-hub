using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuditEventPartitioningTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_migrations_provision_partitions_for_the_current_month_and_the_two_following()
    {
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            List<string> partitions = await ListPartitionsAsync(db);

            DateTimeOffset now = DateTimeOffset.UtcNow;
            for (var offset = 0; offset <= 2; offset++)
            {
                partitions.ShouldContain($"audit.{MonthPartitionName(now, offset)}");
            }
        });
    }

    [RequiresDockerFact]
    public async Task A_governed_effect_lands_its_audit_event_in_the_partition_of_the_occurrence_month()
    {
        HttpClient author = fixture.CreateAuthorClient("author-part-1");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var partition = await PartitionHoldingAsync(db, key);
            partition.ShouldBe($"audit.{MonthPartitionName(DateTimeOffset.UtcNow, 0)}");
        });
    }

    [RequiresDockerFact]
    public async Task The_append_only_trigger_rejects_updates_issued_directly_against_a_partition()
    {
        HttpClient author = fixture.CreateAuthorClient("author-part-2");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var partition = await PartitionHoldingAsync(db, key);
            var update = $"UPDATE {partition} SET actor_id = 'tampered' WHERE entity_id = '{key}'";
            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlRawAsync(update));
            exception.Message.ShouldContain("append-only");
        });
    }

    [RequiresDockerFact]
    public async Task The_append_only_trigger_rejects_deletes_issued_directly_against_a_partition()
    {
        HttpClient author = fixture.CreateAuthorClient("author-part-3");
        var key = await TemplateApi.CreateTemplateAsync(author, TemplateApi.NewKey());

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var partition = await PartitionHoldingAsync(db, key);
            var delete = $"DELETE FROM {partition} WHERE entity_id = '{key}'";
            PostgresException exception = await Should.ThrowAsync<PostgresException>(
                () => db.Database.ExecuteSqlRawAsync(delete));
            exception.Message.ShouldContain("append-only");
        });
    }

    [RequiresDockerFact]
    public async Task The_partition_manager_recreates_a_missing_future_partition_and_a_second_run_changes_nothing()
    {
        var futurePartition = MonthPartitionName(DateTimeOffset.UtcNow, 2);
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var drop = $"DROP TABLE IF EXISTS audit.{futurePartition}";
            await db.Database.ExecuteSqlRawAsync(drop);
        });

        await using ServiceProvider provider = CreateMaintenanceProvider();
        var createdOnFirstRun = await RunRoundAsync(provider);
        var createdOnSecondRun = await RunRoundAsync(provider);

        createdOnFirstRun.ShouldBe(1);
        createdOnSecondRun.ShouldBe(0);
        await fixture.ExecuteAuditDbAsync(async db =>
            (await ListPartitionsAsync(db)).ShouldContain($"audit.{futurePartition}"));
    }

    [RequiresDockerFact]
    public async Task The_closing_cycle_stays_inactive_while_its_gate_is_off()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using ServiceProvider provider = CreateMaintenanceProvider(loggerProvider: loggerProvider);

        await RunRoundAsync(provider);

        loggerProvider.Messages.ShouldContain(message =>
            message.Contains("Ciclo de retenção inativo", StringComparison.Ordinal));
        loggerProvider.Messages.ShouldNotContain(message =>
            message.Contains("destacada", StringComparison.Ordinal));
    }

    [RequiresDockerFact]
    public async Task The_closing_cycle_refuses_to_close_a_partition_while_the_write_revoke_gate_is_off()
    {
        var loggerProvider = new CapturingLoggerProvider();
        await using ServiceProvider provider = CreateMaintenanceProvider(
            new Dictionary<string, string?>
            {
                ["Modules:Audit:PartitionManager:EnableRetentionCycle"] = "true",
                ["Modules:Audit:PartitionManager:ClosingGraceDays"] = "0",
            },
            loggerProvider);

        MonthlyPartitionWindow past = MonthlyPartitions.Plan(
            "audit_event", DateTimeOffset.UtcNow.AddMonths(-3), 0)[0];
        IReadOnlyList<PartitionClosingOutcome> outcomes;
        using (IServiceScope scope = provider.CreateScope())
        {
            outcomes = await scope.ServiceProvider
                .GetRequiredService<PartitionClosingCycle>()
                .RunAsync([past], [], CancellationToken.None);
        }

        // A partition that can still receive rows must never be declared
        // final, so the cycle stops before touching anything.
        outcomes.ShouldHaveSingleItem().Failure.ShouldBe("revoke-gate-disabled");
        loggerProvider.Messages.ShouldContain(message =>
            message.Contains("Etapa de REVOKE", StringComparison.Ordinal)
            && message.Contains("inativa", StringComparison.Ordinal));
        loggerProvider.Messages.ShouldNotContain(message =>
            message.Contains("destacada", StringComparison.Ordinal));
    }

    private ServiceProvider CreateMaintenanceProvider(
        Dictionary<string, string?>? overrides = null,
        CapturingLoggerProvider? loggerProvider = null)
        => AuditMaintenanceComposition.Build(
            fixture.PostgresConnectionString,
            overrides,
            services => services.AddSingleton<IWormObjectStore, InMemoryWormObjectStore>(),
            loggerProvider);

    private static async Task<int> RunRoundAsync(ServiceProvider provider)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<PartitionMaintenanceRound>()
            .RunAsync(CancellationToken.None);
    }

    private static string MonthPartitionName(DateTimeOffset reference, int monthsAhead)
    {
        DateTime month = new DateTime(
            reference.UtcDateTime.Year, reference.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(monthsAhead);
        return $"audit_event_{month.Year:D4}_{month.Month:D2}";
    }

    private static async Task<List<string>> ListPartitionsAsync(
        AuditDbContext db)
        => await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT inhrelid::regclass::text AS "Value"
                 FROM pg_inherits
                 WHERE inhparent = 'audit.audit_event'::regclass
                 """)
            .ToListAsync();

    private static async Task<string> PartitionHoldingAsync(
        AuditDbContext db,
        string entityId)
        => await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT tableoid::regclass::text AS "Value"
                 FROM audit.audit_event
                 WHERE entity_id = {entityId}
                 """)
            .SingleAsync();

}
