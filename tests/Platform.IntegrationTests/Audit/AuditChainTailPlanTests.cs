using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Which plan the appender's tail read gets once the partition has enough rows
/// for the choice to mean anything. On an almost empty partition a sequential
/// scan is the cheap and correct plan, so a planner assertion there would grade
/// the size of the table instead of the index; this class therefore owns a
/// database of its own, loads a partition, and reads the plan on both sides of
/// the index.
/// </summary>
public sealed class AuditChainTailPlanTests : IAsyncLifetime
{
    private const int SeededRows = 20_000;

    private const string TailSql = """
        SELECT hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive AND hash IS NOT NULL
        ORDER BY seq DESC
        LIMIT 1
        """;

    /// <summary>
    /// The chained half of the range read the verification and the export
    /// share, as the reader sends it: predicate first so the partial index can
    /// match, then the key bounds, then a bounded block.
    /// </summary>
    private const string ChainedRangeSql = """
        SELECT id, seq, occurred_at, actor_type, actor_id, application, action,
               entity_type, entity_id, details::text, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND hash IS NOT NULL
          AND seq > 0 AND seq <= 9223372036854775807
        ORDER BY seq
        LIMIT 5000
        """;

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (DockerEnvironment.IsAvailable)
        {
            await _postgres.StartAsync();
        }
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    [RequiresDockerFact]
    public async Task The_planner_answers_the_tail_read_with_the_index_and_scans_the_partition_without_it()
    {
        await MigrateAsync();
        await using AuditDbContext db = CreateAuditContext();
        await SeedCurrentPartitionAsync(db);

        IReadOnlyList<string> withIndex = await ExplainTailAsync(db);

        // The plan is the oracle, not the presence of the index: an index the
        // planner refuses to use costs every write and buys nothing, which is
        // exactly what the composite shape did. The node names the index of the
        // partition, which PostgreSQL derives from the partition and the
        // column when it propagates the parent's index, not the parent's name.
        withIndex.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && line.Contains("seq_idx", StringComparison.Ordinal),
            Plan(withIndex));
        withIndex.ShouldNotContain(line => line.Contains("Seq Scan", StringComparison.Ordinal), Plan(withIndex));

        await db.Database.ExecuteSqlRawAsync("DROP INDEX audit.ix_audit_event_chain_tail");
        IReadOnlyList<string> withoutIndex = await ExplainTailAsync(db);

        // The same read without the index is the state this change corrects,
        // and measuring it here is what keeps the assertion above from being a
        // sentence that passes no matter what the schema holds.
        withoutIndex.ShouldContain(
            line => line.Contains("Seq Scan", StringComparison.Ordinal), Plan(withoutIndex));
    }

    [RequiresDockerFact]
    public async Task The_range_read_walks_the_index_in_order_and_stops_sorting_the_partition()
    {
        await MigrateAsync();
        await using AuditDbContext db = CreateAuditContext();
        await SeedCurrentPartitionAsync(db);

        IReadOnlyList<string> withIndex = await ExplainAsync(db, ChainedRangeSql);

        // The claim this shape has to pay off: the sort disappears. It was the
        // expensive half, because it carried the canonical text of every row of
        // the partition through an external merge on disk.
        withIndex.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && line.Contains("seq_idx", StringComparison.Ordinal),
            Plan(withIndex));
        withIndex.ShouldNotContain(line => line.Contains("Sort", StringComparison.Ordinal), Plan(withIndex));
        withIndex.ShouldNotContain(line => line.Contains("Seq Scan", StringComparison.Ordinal), Plan(withIndex));

        await db.Database.ExecuteSqlRawAsync("DROP INDEX audit.ix_audit_event_chain_tail");
        IReadOnlyList<string> withoutIndex = await ExplainAsync(db, ChainedRangeSql);

        // Without the index the same statement goes back to scanning and
        // ordering, which is what the assertion above would otherwise be free
        // to pass on any schema at all.
        withoutIndex.ShouldContain(
            line => line.Contains("Sort", StringComparison.Ordinal), Plan(withoutIndex));
    }

    private async Task MigrateAsync()
    {
        DbContextOptions<TemplateManagementDbContext> templateOptions =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "templatemanagement"))
                .Options;
        await using (var templates = new TemplateManagementDbContext(templateOptions))
        {
            await templates.Database.MigrateAsync();
        }

        await using AuditDbContext audit = CreateAuditContext();
        await audit.Database.MigrateAsync();
    }

    private AuditDbContext CreateAuditContext()
    {
        DbContextOptions<AuditDbContext> options =
            new DbContextOptionsBuilder<AuditDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "audit"))
                .Options;
        return new AuditDbContext(options);
    }

    /// <summary>
    /// Loads the current month with chained rows. The hashes are fabricated on
    /// purpose: nothing in this database verifies the chain, and what the plan
    /// depends on is how many rows carry a hash at all.
    /// </summary>
    private static async Task SeedCurrentPartitionAsync(AuditDbContext db)
    {
        var seed = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             INSERT INTO audit.audit_event
                 (id, seq, occurred_at, actor_type, actor_id, application, action,
                  entity_type, entity_id, details, canonical, prev_hash, hash)
             SELECT
                 gen_random_uuid(),
                 nextval(pg_get_serial_sequence('audit.audit_event', 'seq')),
                 date_trunc('month', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
                     + (n * interval '1 second'),
                 'system', 'tail-plan-seed', NULL, 'notification.accepted',
                 'notification', 'seed-' || n, json_build_object('origin', 'tail-plan-seed')::jsonb,
                 'seed-' || n, sha256(n::text::bytea), sha256((n + 1)::text::bytea)
             FROM generate_series(1, {SeededRows}) AS n
             """);
        await db.Database.ExecuteSqlRawAsync(seed);

        MonthlyPartitionWindow window = MonthlyPartitions.Plan("audit_event", DateTimeOffset.UtcNow, 0)[0];
        var analyze = $"""ANALYZE audit."{window.PartitionName}" """;
        await db.Database.ExecuteSqlRawAsync(analyze);
    }

    private static Task<IReadOnlyList<string>> ExplainTailAsync(AuditDbContext db) => ExplainAsync(db, TailSql);

    private static async Task<IReadOnlyList<string>> ExplainAsync(AuditDbContext db, string sql)
    {
        MonthlyPartitionWindow window = MonthlyPartitions.Plan("audit_event", DateTimeOffset.UtcNow, 0)[0];
        await db.Database.OpenConnectionAsync();
        DbConnection connection = db.Database.GetDbConnection();
        await using DbCommand command = connection.CreateCommand();
        command.CommandText = "EXPLAIN " + sql;
        AddParameter(command, "fromInclusive", ToInstant(window.FromInclusive));
        AddParameter(command, "toExclusive", ToInstant(window.ToExclusive));

        var lines = new List<string>();
        await using (DbDataReader reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }
        }

        await db.Database.CloseConnectionAsync();
        return lines;
    }

    /// <summary>The plan itself, so a failure reports what the planner chose instead of only what it did not.</summary>
    private static string Plan(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines);

    private static void AddParameter(DbCommand command, string name, object value)
    {
        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static DateTimeOffset ToInstant(DateOnly day)
        => new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
