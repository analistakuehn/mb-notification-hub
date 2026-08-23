using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
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
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var futurePartition = MonthPartitionName(DateTimeOffset.UtcNow, 2);
            var drop = $"DROP TABLE IF EXISTS audit.{futurePartition}";
            await db.Database.ExecuteSqlRawAsync(drop);

            PartitionMaintenance maintenance = CreateMaintenance(
                db, NullLogger<PartitionMaintenance>.Instance, new PartitionManagerOptions());

            var createdOnFirstRun = await maintenance.RunAsync(CancellationToken.None);
            var createdOnSecondRun = await maintenance.RunAsync(CancellationToken.None);

            createdOnFirstRun.ShouldBe(1);
            createdOnSecondRun.ShouldBe(0);
            (await ListPartitionsAsync(db)).ShouldContain($"audit.{futurePartition}");
        });
    }

    [RequiresDockerFact]
    public async Task The_disabled_phase_gates_execute_nothing_and_log_their_inactive_state()
    {
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var logger = new CapturingLogger<PartitionMaintenance>();
            PartitionMaintenance maintenance = CreateMaintenance(db, logger, new PartitionManagerOptions());

            await maintenance.RunAsync(CancellationToken.None);

            logger.Messages.ShouldContain(message =>
                message.Contains("REVOKE", StringComparison.Ordinal)
                && message.Contains("inativa", StringComparison.Ordinal));
            logger.Messages.ShouldContain(message =>
                message.Contains("retenção", StringComparison.Ordinal)
                && message.Contains("inativo", StringComparison.Ordinal));
            logger.Messages.ShouldNotContain(message =>
                message.Contains("não implementad", StringComparison.Ordinal));
        });
    }

    [RequiresDockerFact]
    public async Task Enabling_the_phase_gates_only_reports_the_steps_as_not_yet_implemented()
    {
        await fixture.ExecuteAuditDbAsync(async db =>
        {
            var logger = new CapturingLogger<PartitionMaintenance>();
            PartitionMaintenance maintenance = CreateMaintenance(db, logger, new PartitionManagerOptions
            {
                EnableRevokeOnClosedPartitions = true,
                EnableRetentionCycle = true,
            });

            await maintenance.RunAsync(CancellationToken.None);

            logger.Messages.ShouldContain(message =>
                message.Contains("REVOKE", StringComparison.Ordinal)
                && message.Contains("não implementada", StringComparison.Ordinal));
            logger.Messages.ShouldContain(message =>
                message.Contains("retenção", StringComparison.Ordinal)
                && message.Contains("não implementado", StringComparison.Ordinal));
        });
    }

    private static PartitionMaintenance CreateMaintenance(
        AuditDbContext db,
        ILogger<PartitionMaintenance> logger,
        PartitionManagerOptions options)
        => new(db, Options.Create(options), TimeProvider.System, logger);

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

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;

        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, exception));
    }
}
