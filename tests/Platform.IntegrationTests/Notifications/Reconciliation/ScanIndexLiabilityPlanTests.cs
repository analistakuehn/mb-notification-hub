using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Notifications.Reconciliation;

/// <summary>
/// What the retirement of the scheduler's dead index entries costs, and what
/// it saves, measured rather than asserted.
/// <para>
/// The debt is invisible to every functional test: the deadline scan already
/// keeps concluded notifications out of its batch, so nothing is asked twice
/// and nobody is messaged twice. What grows is the work underneath, one index
/// entry read and discarded per round per row, forever. Only the plan can see
/// it, so the plan is what this class reads, against the statements the code
/// actually sends and never a copy of them.
/// </para>
/// <para>
/// A database of this class's own, and a table large enough that a sequential
/// walk is never the cheaper plan: an index assertion on an almost empty table
/// grades the size of the table.
/// </para>
/// </summary>
[Collection(QueryPlanCollectionDefinition.Name)]
public sealed partial class ScanIndexLiabilityPlanTests : IAsyncLifetime
{
    private const int SeededAttempts = 40_000;

    /// <summary>
    /// Attempts of notifications that already ended while their own status
    /// stayed where the provider left it. That pair, a stamped deadline and an
    /// empty plan claim, is the predicate of the index, so these are the rows
    /// the deadline scan reads and throws away once per round.
    /// </summary>
    private const int Liability = 800;

    private const string AttemptTable = "notification_attempt";
    private const string OverdueIndex = "ix_notification_attempt_fallback_due";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable) return;

        await _postgres.StartAsync();
        await using NotificationsDbContext db = CreateContext();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    /// <summary>
    /// The measurement that justifies the retirement existing at all: how many
    /// index entries the deadline scan reads per round, before and after.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_deadline_scan_stops_reading_the_rows_of_concluded_notifications()
    {
        await using NotificationsDbContext db = CreateContext();
        var alive = await DueEntriesAsync(db, concluded: false);
        var dead = await DueEntriesAsync(db, concluded: true);
        var readBefore = await RowsReadByTheDeadlineScanAsync(db);
        var parkedBefore = await ParkedInIndexAsync(db);

        var retired = await RetireAsync(db);

        retired.ShouldBe(Liability);
        (await ParkedInIndexAsync(db)).ShouldBe(parkedBefore - Liability);
        var readAfter = await RowsReadByTheDeadlineScanAsync(db);

        // The whole measurement in one line: the round was reading the live
        // work plus every dead entry, and it now reads only the live work.
        readBefore.ShouldBe(
            alive + dead,
            $"a varredura de prazo lia {readBefore} entradas de índice por rodada: {alive} de "
            + $"trabalho real e {dead} de notificações já encerradas.");
        readAfter.ShouldBe(
            alive,
            $"depois da retirada a rodada lê {readAfter} entradas, e todas elas são trabalho real.");
    }

    /// <summary>
    /// The retirement reads the same partial index the scheduler reads, and it
    /// only does so while the statement carries the predicate literally: the
    /// statement has no equality to seek by, so the implication of the filter
    /// is the whole of what keeps this off a walk of every partition.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_planner_answers_the_retirement_with_the_index_and_walks_the_table_without_it()
    {
        await using NotificationsDbContext db = CreateContext();
        IReadOnlyList<string> withIndex = await ExplainRetirementAsync(db, ScanIndexLiabilitySweep.RetireSql);

        await ShouldUseAsync(db, withIndex, OverdueIndex, AttemptTable);

        await db.Database.ExecuteSqlRawAsync($"DROP INDEX notifications.{OverdueIndex}");
        IReadOnlyList<string> withoutIndex = await ExplainRetirementAsync(
            db, ScanIndexLiabilitySweep.RetireSql);
        await ShouldWalkAsync(db, withoutIndex, AttemptTable);

        await db.Database.ExecuteSqlRawAsync(
            $"""
            CREATE INDEX {OverdueIndex} ON notifications.notification_attempt
                (status, fallback_deadline)
            WHERE fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL
              AND fallback_requested_at IS NULL
            """);
        await ShouldUseAsync(
            db,
            await ExplainRetirementAsync(db, ScanIndexLiabilitySweep.RetireSql),
            OverdueIndex,
            AttemptTable);
    }

    /// <summary>
    /// The mutation this measurement has to survive: without the literal
    /// predicate the retirement still selects the same rows and the planner
    /// stops being able to prove it, which is the defect that costs a round of
    /// measurement every time it is rediscovered.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_planner_ignores_the_index_when_the_retirement_stops_carrying_its_predicate()
    {
        await using NotificationsDbContext db = CreateContext();
        await ShouldUseAsync(
            db,
            await ExplainRetirementAsync(db, ScanIndexLiabilitySweep.RetireSql),
            OverdueIndex,
            AttemptTable);

        var withoutPredicate = WithoutLines(
            ScanIndexLiabilitySweep.RetireSql,
            "attempt.plan_advanced_at IS NULL",
            "attempt.fallback_requested_at IS NULL");
        withoutPredicate.ShouldNotBe(
            ScanIndexLiabilitySweep.RetireSql,
            "a mutação não encontrou o predicado; sem ela este teste não prova nada.");

        IReadOnlyList<string> degraded = await ExplainRetirementAsync(db, withoutPredicate);

        degraded.ShouldNotContain(
            line => line.Contains(OverdueIndex, StringComparison.Ordinal),
            Plan(degraded));
        await ShouldWalkAsync(db, degraded, AttemptTable);
    }

    private static async Task<int> RetireAsync(NotificationsDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = ScanIndexLiabilitySweep.RetireSql;
            ScanCommands.AddParameter(command, "now", DateTimeOffset.UtcNow);
            ScanCommands.AddParameter(command, "attemptWindow", NotificationPlanOutcome.AttemptWindow);
            ScanCommands.AddParameter(command, "batchSize", 100_000);
            return await command.ExecuteNonQueryAsync();
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// Entries of the deadline index the scan really walks in a round, split by
    /// whether their notification already ended. The deadline has to be past
    /// for the scan to reach the entry at all, which is why a row parked with a
    /// future deadline counts as neither.
    /// </summary>
    private static async Task<int> DueEntriesAsync(NotificationsDbContext db, bool concluded)
        => await db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM notifications.notification_attempt AS attempt
                JOIN notifications.notification AS notification
                  ON notification.id = attempt.notification_id
                WHERE attempt.status = 'sent'
                  AND attempt.fallback_deadline IS NOT NULL
                  AND attempt.plan_advanced_at IS NULL
                  AND attempt.fallback_requested_at IS NULL
                  AND attempt.fallback_deadline < now()
                  AND (notification.status <> 'dispatched') = {concluded}
                """)
            .SingleAsync();

    private static async Task<int> ParkedInIndexAsync(NotificationsDbContext db)
        => await db.Database
            .SqlQuery<int>(
                $"""
                SELECT count(*)::int AS "Value"
                FROM notifications.notification_attempt
                WHERE fallback_deadline IS NOT NULL
                  AND plan_advanced_at IS NULL
                  AND fallback_requested_at IS NULL
                """)
            .SingleAsync();

    /// <summary>
    /// Index entries the deadline claim actually walks in one round, read from
    /// the executed plan. It is the number the retirement exists to lower, and
    /// no functional assertion can see it.
    /// </summary>
    private static async Task<int> RowsReadByTheDeadlineScanAsync(NotificationsDbContext db)
    {
        IReadOnlyList<string> plan = await ExplainAsync(
            db,
            OverdueFallbackScan.DeadlineClaimSql,
            command =>
            {
                ScanCommands.AddParameter(command, "now", DateTimeOffset.UtcNow);
                ScanCommands.AddParameter(command, "attemptWindow", NotificationPlanOutcome.AttemptWindow);
                ScanCommands.AddParameter(command, "batchSize", 200);
            },
            analyze: true);

        IReadOnlyList<string> children = await ChildIndexesOfAsync(db, OverdueIndex);
        var rows = 0;
        foreach (var line in plan)
        {
            if (!children.Any(child => line.Contains(child, StringComparison.Ordinal))) continue;

            Match match = ActualRows().Match(line);
            if (match.Success) rows += int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }

        return rows;
    }

    /// <summary>
    /// The rows the executor really returned, never the ones it estimated: the
    /// same line carries both, and the estimate is the planner's opinion.
    /// </summary>
    [GeneratedRegex(@"actual time=[^)]*?rows=(\d+)")]
    private static partial Regex ActualRows();

    /// <summary>
    /// The plan names a child of the parent index, never the parent: an index
    /// on a partitioned table is a template, and the executor scans the
    /// per-partition indexes PostgreSQL named itself.
    /// </summary>
    private static async Task ShouldUseAsync(
        NotificationsDbContext db,
        IReadOnlyList<string> plan,
        string parentIndex,
        string table)
    {
        IReadOnlyList<string> children = await ChildIndexesOfAsync(db, parentIndex);
        children.ShouldNotBeEmpty($"o índice '{parentIndex}' não tem índices de partição.");
        plan.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && children.Any(child => line.Contains(child, StringComparison.Ordinal)),
            $"o plano não usou nenhum índice filho de '{parentIndex}':{Environment.NewLine}{Plan(plan)}");
        await ShouldNotWalkAsync(db, plan, table);
    }

    /// <summary>
    /// No sequential scan over a partition that holds rows. The qualifier is
    /// not a weakening: a partitioned table always carries empty partitions for
    /// the months ahead, and reading nothing sequentially is the cheapest plan
    /// there is.
    /// </summary>
    private static async Task ShouldNotWalkAsync(
        NotificationsDbContext db,
        IReadOnlyList<string> plan,
        string table)
    {
        IReadOnlyList<string> populated = await PopulatedPartitionsOfAsync(db, table);
        populated.ShouldNotBeEmpty(
            $"nenhuma partição de '{table}' tem linhas; o plano estaria medindo o tamanho da tabela.");
        plan.ShouldNotContain(line => WalksAny(line, populated), Plan(plan));
    }

    private static async Task ShouldWalkAsync(
        NotificationsDbContext db,
        IReadOnlyList<string> plan,
        string table)
    {
        IReadOnlyList<string> populated = await PopulatedPartitionsOfAsync(db, table);
        plan.ShouldContain(line => WalksAny(line, populated), Plan(plan));
    }

    private static bool WalksAny(string line, IReadOnlyList<string> partitions)
        => line.Contains("Seq Scan", StringComparison.Ordinal)
            && partitions.Any(partition => line.Contains(partition, StringComparison.Ordinal));

    private static async Task<IReadOnlyList<string>> PopulatedPartitionsOfAsync(
        NotificationsDbContext db,
        string table)
        => await db.Database
            .SqlQuery<string>(
                $"""
                SELECT child.relname AS "Value"
                FROM pg_inherits
                JOIN pg_class AS child ON child.oid = pg_inherits.inhrelid
                JOIN pg_class AS parent ON parent.oid = pg_inherits.inhparent
                JOIN pg_namespace AS schema ON schema.oid = parent.relnamespace
                WHERE parent.relname = {table}
                  AND schema.nspname = 'notifications'
                  AND child.reltuples > 0
                """)
            .ToListAsync();

    private static async Task<IReadOnlyList<string>> ChildIndexesOfAsync(
        NotificationsDbContext db,
        string parentIndex)
        => await db.Database
            .SqlQuery<string>(
                $"""
                SELECT child.relname AS "Value"
                FROM pg_inherits
                JOIN pg_class AS child ON child.oid = pg_inherits.inhrelid
                JOIN pg_class AS parent ON parent.oid = pg_inherits.inhparent
                JOIN pg_namespace AS schema ON schema.oid = parent.relnamespace
                WHERE parent.relname = {parentIndex} AND schema.nspname = 'notifications'
                """)
            .ToListAsync();

    private static Task<IReadOnlyList<string>> ExplainRetirementAsync(
        NotificationsDbContext db,
        string sql)
        => ExplainAsync(
            db,
            sql,
            command =>
            {
                ScanCommands.AddParameter(command, "now", DateTimeOffset.UtcNow);
                ScanCommands.AddParameter(command, "attemptWindow", NotificationPlanOutcome.AttemptWindow);
                ScanCommands.AddParameter(command, "batchSize", 2_000);
            },
            analyze: false);

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        NotificationsDbContext db,
        string sql,
        Action<DbCommand> bind,
        bool analyze)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var lines = new List<string>();
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

            // A locking clause is not allowed inside EXPLAIN of a statement the
            // planner would have to lock rows for, and it changes no access
            // path: the rows are chosen first and locked afterwards.
            command.CommandText = (analyze ? "EXPLAIN (ANALYZE) " : "EXPLAIN ")
                + WithoutLocking(sql);
            bind(command);
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) lines.Add(reader.GetString(0));

            return lines;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private const char SqlNewline = (char)10;

    private static string WithoutLines(string sql, params string[] fragments)
    {
        IEnumerable<string> kept = sql
            .Split(SqlNewline)
            .Where(line => !fragments.Any(fragment => line.Contains(fragment, StringComparison.Ordinal)));
        return string.Join(SqlNewline, kept);
    }

    private static string WithoutLocking(string sql)
        => sql.Replace("FOR UPDATE OF attempt SKIP LOCKED", string.Empty, StringComparison.Ordinal);

    private static string Plan(IReadOnlyList<string> lines)
        => string.Join(Environment.NewLine, lines);

    private NotificationsDbContext CreateContext()
        => new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"))
            .Options);

    /// <summary>
    /// A backlog in the shape production reaches: most attempts settled, a
    /// small minority still waiting on a deadline, and, among those, the ones
    /// whose notification already ended without ever advancing a plan. The last
    /// group is the debt, and it is the only group the retirement may touch.
    /// </summary>
    private static async Task SeedAsync(NotificationsDbContext db)
    {
        var seedNotifications = string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              INSERT INTO notifications.notification
                  (id, created_at, application, idempotency_key, recipient_id, class, template_key,
                   auth_flow, template_version, policy_version, variables_masked, variables_enc,
                   correlation_id, requested_by, status, release_at, expires_at)
              SELECT
                  gen_random_uuid(), now() - (n * interval '1 second'),
                  'app-liability', 'idem-' || n, 'cus_' || n, 'transactional',
                  'tpl-liability', false, 1, 1, jsonb_build_object(), NULL, NULL, 'liability-tests',
                  CASE
                      WHEN n % 100 = 0 THEN 'expired'
                      WHEN n % 100 = 1 THEN 'failed'
                      ELSE 'dispatched'
                  END,
                  NULL, now() + interval '1 day'
              FROM generate_series(1, {{SeededAttempts}}) AS n
              """);
        await db.Database.ExecuteSqlRawAsync(seedNotifications);

        // One attempt per notification, joined by creation order so the window
        // in the scan's join predicate holds for every pair. A concluded
        // notification keeps its attempt exactly where the provider left it,
        // which is the shape the debt really has: status sent, deadline
        // stamped, plan claim empty.
        var seedAttempts = string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              INSERT INTO notifications.notification_attempt
                  (id, created_at, notification_id, sequence, channel, provider_key, contact_point_id,
                   device_token_id, provider_message_id, rendered_content_enc, content_hash_full,
                   content_hash_masked, status, error_code, fallback_deadline, plan_advanced_at,
                   status_changed_at, fallback_requested_at, sent_at, delivered_at)
              SELECT
                  gen_random_uuid(), notification.created_at, notification.id, 1, 'push', 'fcm',
                  NULL, NULL, NULL, '\x01'::bytea, repeat('a', 64), repeat('a', 64),
                  CASE
                      WHEN notification.status <> 'dispatched'
                        OR row_number() OVER (ORDER BY notification.created_at) % 397 = 0
                      THEN 'sent'
                      ELSE 'delivered'
                  END,
                  NULL,
                  CASE
                      WHEN notification.status <> 'dispatched'
                        OR row_number() OVER (ORDER BY notification.created_at) % 397 = 0
                      THEN notification.created_at + interval '30 seconds'
                      ELSE NULL
                  END,
                  NULL, notification.created_at, NULL, notification.created_at, NULL
              FROM notifications.notification AS notification
              """);
        await db.Database.ExecuteSqlRawAsync(seedAttempts);
        await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.notification");
        await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.notification_attempt");
    }
}
