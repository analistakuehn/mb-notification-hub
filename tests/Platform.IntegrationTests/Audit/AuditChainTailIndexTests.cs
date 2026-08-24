using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// The index that answers the chain tail read taken inside the advisory lock.
/// The cost of that read is the serialization window of a whole monthly
/// partition, so what has to hold is not that an index exists somewhere but
/// that the planner picks it for the statement the appender sends, in every
/// partition the trail will ever write to.
/// </summary>
[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuditChainTailIndexTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task The_migrations_leave_the_partial_tail_index_on_the_partitioned_parent()
    {
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var definition = await SequenceIndexOfAsync(db, "audit_event");

            // Descending on the sequence alone, and partial. A composite led by
            // the partition column is a useless prefix inside a partition, and
            // a non-partial index would answer a statement that no longer
            // carries the filter, hiding the coupling instead of enforcing it.
            definition.ShouldNotBeNull();
            definition.ShouldContain("ix_audit_event_chain_tail");
            definition.ShouldContain("(seq DESC)");
            definition.ShouldContain("WHERE (hash IS NOT NULL)");
        });
    }

    [RequiresDockerFact]
    public async Task A_partition_created_after_the_migration_carries_the_index_too()
    {
        var futurePartition = MonthPartitionName(DateTimeOffset.UtcNow, 2);
        var drop = $"DROP TABLE IF EXISTS audit.{futurePartition}";
        await fixture.ExecuteAuditDbAsync(async db => await db.Database.ExecuteSqlRawAsync(drop));

        await using ServiceProvider provider = AuditMaintenanceComposition.Build(
            fixture.PostgresConnectionString,
            configureServices: services => services.AddSingleton<IWormObjectStore, InMemoryWormObjectStore>());
        using (IServiceScope scope = provider.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<PartitionMaintenanceRound>()
                .RunAsync(CancellationToken.None);
        }

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var definition = await SequenceIndexOfAsync(db, futurePartition);

            // PostgreSQL propagates an index declared on the partitioned parent
            // to every partition created afterwards. Without that, the month
            // the provisioner creates ahead of time would silently reintroduce
            // the scan on the first day of its life.
            definition.ShouldNotBeNull();
            definition.ShouldContain("(seq DESC)");
            definition.ShouldContain("WHERE (hash IS NOT NULL)");
        });
    }

    /// <summary>The one index of the table that orders by the sequence, or null when there is not exactly one.</summary>
    private static async Task<string?> SequenceIndexOfAsync(AuditDbContext db, string table)
    {
        List<string> definitions = await db.Database
            .SqlQuery<string>($"""
                 SELECT indexdef AS "Value"
                 FROM pg_indexes
                 WHERE schemaname = 'audit'
                   AND tablename = {table}
                   AND indexdef LIKE '%(seq DESC)%'
                 """)
            .ToListAsync();
        return definitions.Count == 1 ? definitions[0] : null;
    }

    private static string MonthPartitionName(DateTimeOffset reference, int monthsAhead)
    {
        DateTime month = new DateTime(
            reference.UtcDateTime.Year, reference.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc)
            .AddMonths(monthsAhead);
        return $"audit_event_{month.Year:D4}_{month.Month:D2}";
    }
}
