using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// The reader fetches the rows through two indexed statements and merges them,
/// where it used to run one statement that ordered the whole partition. Only
/// the fetching changed, so what these tests pin is that the rows, their order
/// and their contents are the ones the single statement returned, over a
/// partition that holds both chained and pre-chain rows.
/// </summary>
public sealed class AuditTrailReaderEquivalenceTests : IAsyncLifetime
{
    /// <summary>
    /// The statement the reader used before the split, kept here as the oracle.
    /// Comparing the new reader against it is the only way to say "the same
    /// rows as before" without asserting a list this test wrote itself.
    /// </summary>
    private const string SingleStatementSql = """
        SELECT id, seq, occurred_at, actor_type, actor_id, application, action,
               entity_type, entity_id, details::text, canonical, prev_hash, hash
        FROM audit.audit_event
        WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
          AND seq > @afterSeq AND seq <= @throughSeq
        ORDER BY seq
        LIMIT @maxRows
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
    public async Task The_reader_returns_the_same_rows_in_the_same_order_as_the_single_statement()
    {
        await MigrateAsync();
        await using AuditDbContext db = CreateAuditContext();
        await SeedInterleavedPartitionAsync(db);
        MonthlyPartitionWindow window = CurrentWindow();

        var reader = new AuditTrailReader(db);
        IReadOnlyList<AuditTrailRow> merged =
            await reader.ReadRowsAsync(window, 0, long.MaxValue, 40_000, CancellationToken.None);
        IReadOnlyList<AuditTrailRow> single =
            await ReadWithSingleStatementAsync(db, window, 0, long.MaxValue, 40_000);

        merged.Count.ShouldBe(single.Count);
        Fingerprints(merged).ShouldBe(Fingerprints(single));

        // The fixture has to make the merge do work: if every pre-chain row sat
        // before every chained row, a reader that simply concatenated would
        // pass this test and fail on a partition where they interleave.
        merged.Count(row => row.IsUnchained).ShouldBeGreaterThan(0);
        merged.Count(row => !row.IsUnchained).ShouldBeGreaterThan(0);
        FirstChainedIndex(merged).ShouldBeLessThan(LastUnchainedIndex(merged));
    }

    [RequiresDockerFact]
    public async Task The_reader_honours_the_row_limit_and_the_sequence_bounds_like_the_single_statement()
    {
        await MigrateAsync();
        await using AuditDbContext db = CreateAuditContext();
        await SeedInterleavedPartitionAsync(db);
        MonthlyPartitionWindow window = CurrentWindow();
        var reader = new AuditTrailReader(db);

        // Small pages and a window that starts inside the partition: the block
        // walk has to stitch several statements per half and still land on the
        // same rows as one statement with the same bounds.
        IReadOnlyList<AuditTrailRow> all =
            await reader.ReadRowsAsync(window, 0, long.MaxValue, 40_000, CancellationToken.None);
        var afterSeq = all[10].Seq;
        var throughSeq = all[^10].Seq;

        IReadOnlyList<AuditTrailRow> merged =
            await reader.ReadRowsAsync(window, afterSeq, throughSeq, 25, CancellationToken.None);
        IReadOnlyList<AuditTrailRow> single =
            await ReadWithSingleStatementAsync(db, window, afterSeq, throughSeq, 25);

        merged.Count.ShouldBe(25);
        Fingerprints(merged).ShouldBe(Fingerprints(single));
    }

    [RequiresDockerFact]
    public async Task The_highest_sequence_of_a_partition_that_holds_only_pre_chain_rows_is_the_pre_chain_one()
    {
        await MigrateAsync();
        await using AuditDbContext db = CreateAuditContext();
        await SeedPreChainOnlyAsync(db);
        MonthlyPartitionWindow window = CurrentWindow();

        var reader = new AuditTrailReader(db);
        var highest = await reader.MaxSeqAsync(window, CancellationToken.None);

        // The high-water mark drives what the closing export claims. Reading it
        // from the chained half alone would report zero here and export nothing
        // for a month that was adopted and never written to again.
        highest.ShouldBe(await ScalarAsync(db, window));
        highest.ShouldBeGreaterThan(0);
    }

    /// <summary>
    /// Every column of every row as text, in order. The row is a record, and
    /// record equality compares the hash arrays by reference, so comparing the
    /// records themselves would fail on identical rows and pass on rows whose
    /// bytes differ only where the arrays happen to be shared.
    /// </summary>
    private static IReadOnlyList<string> Fingerprints(IEnumerable<AuditTrailRow> rows)
    =>
    [
        .. rows.Select(row => string.Create(
            CultureInfo.InvariantCulture,
            $"{row.Id:D}|{row.Seq}|{row.OccurredAt.UtcDateTime:O}|{row.ActorType}|{row.ActorId}|"
            + $"{row.Application}|{row.Action}|{row.EntityType}|{row.EntityId}|{row.DetailsJson}|"
            + $"{row.Canonical}|{Hex(row.PrevHash)}|{Hex(row.Hash)}")),
    ];

    private static string Hex(byte[]? value) => value is null ? "none" : Convert.ToHexString(value);

    private static int FirstChainedIndex(IReadOnlyList<AuditTrailRow> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            if (!rows[index].IsUnchained)
            {
                return index;
            }
        }

        return rows.Count;
    }

    private static int LastUnchainedIndex(IReadOnlyList<AuditTrailRow> rows)
    {
        for (var index = rows.Count - 1; index >= 0; index--)
        {
            if (rows[index].IsUnchained)
            {
                return index;
            }
        }

        return -1;
    }

    private static MonthlyPartitionWindow CurrentWindow()
        => MonthlyPartitions.Plan("audit_event", DateTimeOffset.UtcNow, 0)[0];

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
    /// Fills the current month with chained and pre-chain rows whose sequence
    /// values interleave. Production writes every pre-chain row before the
    /// chain exists, so they sit lower; interleaving them here keeps the merge
    /// from being exercised only in the arrangement that hides its bugs.
    /// </summary>
    private static async Task SeedInterleavedPartitionAsync(AuditDbContext db)
    {
        await InsertAsync(db, 1, 400, chained: true);
        await InsertAsync(db, 401, 200, chained: false);
        await InsertAsync(db, 601, 400, chained: true);
        await InsertAsync(db, 1001, 200, chained: false);
        await InsertAsync(db, 1201, 400, chained: true);
        await AnalyzeAsync(db);
    }

    private static async Task SeedPreChainOnlyAsync(AuditDbContext db)
    {
        await InsertAsync(db, 1, 50, chained: false);
        await AnalyzeAsync(db);
    }

    private static async Task InsertAsync(AuditDbContext db, int first, int count, bool chained)
    {
        var chainColumns = chained
            ? "'canonical-' || n, sha256(n::text::bytea), sha256((n + 1)::text::bytea)"
            : "NULL, NULL, NULL";
        var sql = string.Create(
            CultureInfo.InvariantCulture,
            $"""
             INSERT INTO audit.audit_event
                 (id, seq, occurred_at, actor_type, actor_id, application, action,
                  entity_type, entity_id, details, canonical, prev_hash, hash)
             SELECT
                 gen_random_uuid(),
                 n,
                 date_trunc('month', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
                     + (n * interval '1 second'),
                 'system', 'reader-equivalence', NULL, 'notification.accepted',
                 'notification', 'row-' || n, json_build_object('origin', 'reader-equivalence')::jsonb,
                 {chainColumns}
             FROM generate_series({first}, {first + count - 1}) AS n
             """);
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static async Task AnalyzeAsync(AuditDbContext db)
    {
        var analyze = $"""ANALYZE audit."{CurrentWindow().PartitionName}" """;
        await db.Database.ExecuteSqlRawAsync(analyze);
    }

    private static async Task<long> ScalarAsync(AuditDbContext db, MonthlyPartitionWindow window)
    {
        await db.Database.OpenConnectionAsync();
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = """
            SELECT COALESCE(MAX(seq), 0)
            FROM audit.audit_event
            WHERE occurred_at >= @fromInclusive AND occurred_at < @toExclusive
            """;
        AddParameter(command, "fromInclusive", ToInstant(window.FromInclusive));
        AddParameter(command, "toExclusive", ToInstant(window.ToExclusive));
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<IReadOnlyList<AuditTrailRow>> ReadWithSingleStatementAsync(
        AuditDbContext db,
        MonthlyPartitionWindow window,
        long afterSeq,
        long throughSeq,
        int maxRows)
    {
        await db.Database.OpenConnectionAsync();
        await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = SingleStatementSql;
        AddParameter(command, "fromInclusive", ToInstant(window.FromInclusive));
        AddParameter(command, "toExclusive", ToInstant(window.ToExclusive));
        AddParameter(command, "afterSeq", afterSeq);
        AddParameter(command, "throughSeq", throughSeq);
        AddParameter(command, "maxRows", maxRows);

        var rows = new List<AuditTrailRow>();
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new AuditTrailRow(
                reader.GetGuid(0),
                reader.GetInt64(1),
                reader.GetFieldValue<DateTimeOffset>(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.IsDBNull(11) ? null : reader.GetFieldValue<byte[]>(11),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<byte[]>(12)));
        }

        return rows;
    }

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
