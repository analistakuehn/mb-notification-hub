using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Notifications.Scheduling;

/// <summary>
/// What the planner actually does with the scheduler's statements, measured
/// rather than assumed.
/// <para>
/// A partial index only answers a statement whose quals imply its predicate,
/// and nothing about that failure is visible from the outside: the scan keeps
/// returning the same rows, the tests keep passing, and the round quietly
/// becomes a sequential walk of every partition of the table. Reading the plan
/// is the only oracle that sees it, and the plan is read against the statement
/// the code actually sends, never a copy of it, because a plan test over a
/// transcription grades the transcription.
/// </para>
/// <para>
/// Every assertion here is proved falsifiable in the same test that makes it:
/// the index is dropped, or the predicate is removed, and the degraded plan is
/// read back. An oracle that never fails is a sentence, not a measurement. The
/// database is this class's own for the same reason: dropping an index under a
/// shared fixture would break whatever ran next, and a plan read on an almost
/// empty table would grade the size of the table instead of the index.
/// </para>
/// </summary>
[Collection(QueryPlanCollectionDefinition.Name)]
public sealed class SchedulerScanPlanTests : IAsyncLifetime
{
    /// <summary>
    /// Enough rows that a sequential walk is never the cheaper plan, in the
    /// mixture production has: the overdue attempts are the rare ones, because
    /// a rare match is exactly what separates an index scan from a walk.
    /// </summary>
    private const int SeededAttempts = 40_000;

    private const string AttemptTable = "notification_attempt";
    private const string NotificationTable = "notification";

    private const string OverdueIndex = "ix_notification_attempt_fallback_due";
    private const string UnknownIndex = "ix_notification_attempt_unknown_due";
    private const string ReleaseIndex = "ix_notification_release_due";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:17-alpine")
        .Build();

    async Task IAsyncLifetime.InitializeAsync()
    {
        if (!DockerEnvironment.IsAvailable)
        {
            return;
        }

        await _postgres.StartAsync();
        await using NotificationsDbContext db = CreateContext();
        await db.Database.MigrateAsync();
        await SeedAsync(db);
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    [RequiresDockerFact]
    public async Task The_planner_answers_the_deadline_scan_with_the_index_and_walks_the_table_without_it()
    {
        await using NotificationsDbContext db = CreateContext();
        IReadOnlyList<string> withIndex = await ExplainDeadlineScanAsync(db, OverdueFallbackScan.DeadlineClaimSql);

        await ShouldUseAsync(db, withIndex, OverdueIndex, AttemptTable);

        // The same statement without the index is the state this slice
        // corrects, and measuring it is what keeps the assertion above from
        // being a sentence that passes against any schema at all.
        await db.Database.ExecuteSqlRawAsync($"DROP INDEX notifications.{OverdueIndex}");
        IReadOnlyList<string> withoutIndex = await ExplainDeadlineScanAsync(
            db, OverdueFallbackScan.DeadlineClaimSql);
        await ShouldWalkAsync(db, withoutIndex, AttemptTable);

        await RecreateOverdueIndexAsync(db);
        await ShouldUseAsync(
            db,
            await ExplainDeadlineScanAsync(db, OverdueFallbackScan.DeadlineClaimSql),
            OverdueIndex,
            AttemptTable);
    }

    /// <summary>
    /// The predicate proved load-bearing on its own, with the index in place
    /// the whole time. This is the defect that cost a round of measurement in
    /// the previous phase: the index exists, the statement looks right, and the
    /// planner ignores it because the quals no longer imply the filter.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_planner_ignores_the_index_when_the_statement_stops_carrying_its_predicate()
    {
        await using NotificationsDbContext db = CreateContext();
        await ShouldUseAsync(
            db,
            await ExplainDeadlineScanAsync(db, OverdueFallbackScan.DeadlineClaimSql),
            OverdueIndex,
            AttemptTable);

        // One conjunct removed, nothing else. The rows the statement selects do
        // not change, because the two conjuncts it drops are implied by the
        // ones it keeps for every row that matters; what changes is that the
        // planner can no longer prove the implication.
        var withoutPredicate = WithoutLines(
            OverdueFallbackScan.DeadlineClaimSql,
            "plan_advanced_at IS NULL",
            "fallback_requested_at IS NULL");
        withoutPredicate.ShouldNotBe(
            OverdueFallbackScan.DeadlineClaimSql,
            "a mutação não encontrou o predicado; sem ela este teste não prova nada.");

        IReadOnlyList<string> degraded = await ExplainDeadlineScanAsync(db, withoutPredicate);

        degraded.ShouldNotContain(
            line => line.Contains(OverdueIndex, StringComparison.Ordinal),
            Plan(degraded));
        await ShouldWalkAsync(db, degraded, AttemptTable);
    }

    /// <summary>
    /// The deadline half of the inconclusive rows is seekable by both partial
    /// indexes, because its quals imply both predicates, and the planner takes
    /// the age one: that filter pins the same single status this statement
    /// does, which makes it the narrower of the two. The choice is measured
    /// rather than asserted from the shape of the statement, and what the
    /// falsification proves is the part that matters either way: drop one
    /// shared conjunct and neither index is provable anymore.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_planner_answers_the_unknown_deadline_scan_with_a_partial_index()
    {
        await using NotificationsDbContext db = CreateContext();
        await ShouldUseAsync(
            db,
            await ExplainDeadlineScanAsync(db, OverdueFallbackScan.UnknownDeadlineClaimSql),
            UnknownIndex,
            AttemptTable);

        var withoutPredicate = WithoutLines(
            OverdueFallbackScan.UnknownDeadlineClaimSql,
            "plan_advanced_at IS NULL",
            "fallback_requested_at IS NULL");
        withoutPredicate.ShouldNotBe(
            OverdueFallbackScan.UnknownDeadlineClaimSql,
            "a mutação não encontrou o predicado; sem ela este teste não prova nada.");

        IReadOnlyList<string> degraded = await ExplainDeadlineScanAsync(db, withoutPredicate);

        degraded.ShouldNotContain(
            line => line.Contains(OverdueIndex, StringComparison.Ordinal)
                || line.Contains(UnknownIndex, StringComparison.Ordinal),
            Plan(degraded));
        await ShouldWalkAsync(db, degraded, AttemptTable);
    }

    [RequiresDockerFact]
    public async Task The_planner_seeks_the_age_of_an_inconclusive_verdict_only_while_its_index_exists()
    {
        await using NotificationsDbContext db = CreateContext();
        IReadOnlyList<string> withIndex = await ExplainUnknownScanAsync(
            db, OverdueFallbackScan.UnknownClaimSql);
        await ShouldUseAsync(db, withIndex, UnknownIndex, AttemptTable);

        // The age is answered by the index rather than filtered after it, and
        // that is the whole value of this one.
        withIndex.ShouldContain(
            line => line.Contains("Index Cond: (status_changed_at", StringComparison.Ordinal),
            Plan(withIndex));

        await db.Database.ExecuteSqlRawAsync($"DROP INDEX notifications.{UnknownIndex}");
        IReadOnlyList<string> withoutIndex = await ExplainUnknownScanAsync(
            db, OverdueFallbackScan.UnknownClaimSql);

        // Not a sequential walk, and saying so is the honest measurement: the
        // overdue index survives the drop and this statement implies its
        // predicate too, so the planner falls back to it. What is lost is the
        // age exactly. It stops being a seek and becomes a filter, so the scan
        // reads every attempt ever parked on an inconclusive verdict in order
        // to discard the young ones.
        withoutIndex.ShouldNotContain(
            line => line.Contains("Index Cond: (status_changed_at", StringComparison.Ordinal),
            Plan(withoutIndex));

        await RecreateUnknownIndexAsync(db);
        await ShouldUseAsync(
            db,
            await ExplainUnknownScanAsync(db, OverdueFallbackScan.UnknownClaimSql),
            UnknownIndex,
            AttemptTable);
    }

    [RequiresDockerFact]
    public async Task The_planner_ignores_the_unknown_index_when_the_statement_drops_its_status()
    {
        await using NotificationsDbContext db = CreateContext();
        await ShouldUseAsync(
            db,
            await ExplainUnknownScanAsync(db, OverdueFallbackScan.UnknownClaimSql),
            UnknownIndex,
            AttemptTable);

        // The status written as a bind value instead of a literal. It selects
        // the same rows for every execution this scan ever performs, and the
        // planner still cannot prove that a parameter equals 'unknown'.
        var parameterized = OverdueFallbackScan.UnknownClaimSql
            .Replace("attempt.status = 'unknown'", "attempt.status = @status", StringComparison.Ordinal);
        parameterized.ShouldNotBe(OverdueFallbackScan.UnknownClaimSql);

        IReadOnlyList<string> degraded = await ExplainUnknownScanAsync(
            db, parameterized, bindStatus: true);

        degraded.ShouldNotContain(
            line => line.Contains(UnknownIndex, StringComparison.Ordinal),
            Plan(degraded));
    }

    [RequiresDockerFact]
    public async Task The_planner_answers_the_release_scan_with_the_index_and_walks_the_table_without_it()
    {
        await using NotificationsDbContext db = CreateContext();
        await ShouldUseAsync(
            db,
            await ExplainReleaseScanAsync(db, DeferredReleaseScan.CandidateSql),
            ReleaseIndex,
            NotificationTable);

        await db.Database.ExecuteSqlRawAsync($"DROP INDEX notifications.{ReleaseIndex}");
        IReadOnlyList<string> withoutIndex = await ExplainReleaseScanAsync(
            db, DeferredReleaseScan.CandidateSql);
        await ShouldWalkAsync(db, withoutIndex, NotificationTable);

        await RecreateReleaseIndexAsync(db);
        await ShouldUseAsync(
            db,
            await ExplainReleaseScanAsync(db, DeferredReleaseScan.CandidateSql),
            ReleaseIndex,
            NotificationTable);
    }

    /// <summary>
    /// The plan names a child of the parent index, never the parent. An index
    /// created on a partitioned table is a template: PostgreSQL builds one
    /// index per partition with a name of its own choosing and the executor
    /// scans those. Asserting the parent's name would fail against a correct
    /// schema, so the children are resolved from the catalogue and the plan is
    /// asked whether it named any of them.
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
    /// No sequential scan over a partition that holds rows.
    /// <para>
    /// The qualifier is not a weakening. A partitioned table always carries
    /// empty partitions for the months ahead, and the planner reads an empty
    /// partition sequentially because there is nothing cheaper than reading
    /// nothing. Asserting the absence of the words would therefore fail against
    /// a perfectly indexed schema, which is the kind of oracle that gets
    /// deleted the first time it cries wolf. What matters, and what this asks,
    /// is that no partition with rows in it is walked.
    /// </para>
    /// </summary>
    private static async Task ShouldNotWalkAsync(
        NotificationsDbContext db,
        IReadOnlyList<string> plan,
        string table)
    {
        IReadOnlyList<string> populated = await PopulatedPartitionsOfAsync(db, table);
        populated.ShouldNotBeEmpty(
            $"nenhuma partição de '{table}' tem linhas; o plano estaria medindo o tamanho da "
            + "tabela e não o índice.");
        plan.ShouldNotContain(
            line => WalksAny(line, populated),
            Plan(plan));
    }

    private static async Task ShouldWalkAsync(
        NotificationsDbContext db,
        IReadOnlyList<string> plan,
        string table)
    {
        IReadOnlyList<string> populated = await PopulatedPartitionsOfAsync(db, table);
        plan.ShouldContain(
            line => WalksAny(line, populated),
            Plan(plan));
    }

    private static bool WalksAny(string line, IReadOnlyList<string> partitions)
        => line.Contains("Seq Scan", StringComparison.Ordinal)
            && partitions.Any(partition => line.Contains(partition, StringComparison.Ordinal));

    /// <summary>Partitions of one table that actually hold rows, as the planner sees them.</summary>
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

    private static Task<IReadOnlyList<string>> ExplainDeadlineScanAsync(
        NotificationsDbContext db,
        string sql)
        => ExplainAsync(db, sql, command =>
        {
            ScanCommands.AddParameter(command, "now", DateTimeOffset.UtcNow);
            ScanCommands.AddParameter(command, "attemptWindow", NotificationPlanOutcome.AttemptWindow);
            ScanCommands.AddParameter(command, "batchSize", 200);
        });

    private static Task<IReadOnlyList<string>> ExplainUnknownScanAsync(
        NotificationsDbContext db,
        string sql,
        bool bindStatus = false)
        => ExplainAsync(db, sql, command =>
        {
            ScanCommands.AddParameter(command, "threshold", DateTimeOffset.UtcNow.AddMinutes(-1));
            ScanCommands.AddParameter(command, "attemptWindow", NotificationPlanOutcome.AttemptWindow);
            ScanCommands.AddParameter(command, "batchSize", 200);
            if (bindStatus)
            {
                ScanCommands.AddParameter(command, "status", "unknown");
            }
        });

    private static Task<IReadOnlyList<string>> ExplainReleaseScanAsync(
        NotificationsDbContext db,
        string sql)
        => ExplainAsync(db, sql, command =>
        {
            ScanCommands.AddParameter(command, "now", DateTimeOffset.UtcNow);
            ScanCommands.AddParameter(command, "batchSize", 200);
        });

    private static async Task<IReadOnlyList<string>> ExplainAsync(
        NotificationsDbContext db,
        string sql,
        Action<DbCommand> bind)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            var lines = new List<string>();
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();

            // A locking clause is not allowed inside EXPLAIN of a statement the
            // planner would have to lock rows for, and it changes no access
            // path: the rows are chosen first and locked afterwards.
            command.CommandText = "EXPLAIN " + WithoutLocking(sql);
            bind(command);
            await using DbDataReader reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                lines.Add(reader.GetString(0));
            }

            return lines;
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// The statement without the lines carrying the given fragments, built line
    /// by line instead of by matching an exact block: the newline inside a raw
    /// string literal is the one the source file happens to carry, and a
    /// mutation that silently stops mutating is an oracle that always passes.
    /// </summary>
    private const char SqlNewline = (char)10;

    private static string WithoutLines(string sql, params string[] fragments)
    {
        IEnumerable<string> kept = sql
            .Split(SqlNewline)
            .Where(line => !fragments.Any(fragment => line.Contains(fragment, StringComparison.Ordinal)));
        return string.Join(SqlNewline, kept);
    }

    private static string WithoutLocking(string sql)
        => sql
            .Replace("FOR UPDATE OF attempt SKIP LOCKED", string.Empty, StringComparison.Ordinal)
            .Replace("FOR UPDATE SKIP LOCKED", string.Empty, StringComparison.Ordinal);

    private static string Plan(IReadOnlyList<string> lines)
        => string.Join(Environment.NewLine, lines);

    private NotificationsDbContext CreateContext()
        => new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"))
            .Options);

    /// <summary>
    /// A backlog in the shape production has: most attempts already settled,
    /// a small minority still waiting on a deadline, a smaller one parked on an
    /// inconclusive verdict, and a handful of notifications still deferred.
    /// The scans are interesting exactly because their matches are rare.
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
                  'app-plan', 'idem-' || n, 'cus_' || n,
                  CASE WHEN n % 4 = 0 THEN 'critical' ELSE 'transactional' END,
                  'tpl-plan', n % 40 = 0, 1, 1, jsonb_build_object(), NULL, NULL, 'plan-tests',
                  CASE WHEN n % 500 = 0 THEN 'deferred' ELSE 'dispatched' END,
                  CASE WHEN n % 500 = 0 THEN now() - interval '1 minute' ELSE NULL END,
                  now() + interval '1 day'
              FROM generate_series(1, {{SeededAttempts}}) AS n
              """);
        await db.Database.ExecuteSqlRawAsync(seedNotifications);

        // One attempt per notification, joined by creation order so the window
        // in the scan's join predicate holds for every pair.
        var seedAttempts = """
            INSERT INTO notifications.notification_attempt
                (id, created_at, notification_id, sequence, channel, provider_key, contact_point_id,
                 device_token_id, provider_message_id, rendered_content_enc, content_hash_full,
                 content_hash_masked, status, error_code, fallback_deadline, plan_advanced_at,
                 status_changed_at, fallback_requested_at, sent_at, delivered_at)
            SELECT
                gen_random_uuid(), notification.created_at, notification.id, 1, 'push', 'fcm',
                NULL, NULL, NULL, '\x01'::bytea, repeat('a', 64), repeat('a', 64),
                CASE
                    WHEN row_number() OVER (ORDER BY notification.created_at) % 200 = 0 THEN 'sent'
                    WHEN row_number() OVER (ORDER BY notification.created_at) % 331 = 0 THEN 'unknown'
                    WHEN row_number() OVER (ORDER BY notification.created_at) % 457 = 0 THEN 'queued'
                    WHEN row_number() OVER (ORDER BY notification.created_at) % 613 = 0 THEN 'sending'
                    ELSE 'delivered'
                END,
                NULL,
                CASE
                    WHEN row_number() OVER (ORDER BY notification.created_at) % 200 = 0
                      OR row_number() OVER (ORDER BY notification.created_at) % 331 = 0
                      OR row_number() OVER (ORDER BY notification.created_at) % 457 = 0
                      OR row_number() OVER (ORDER BY notification.created_at) % 613 = 0
                    THEN notification.created_at + interval '30 seconds'
                    ELSE NULL
                END,
                NULL, notification.created_at, NULL, NULL, NULL
            FROM notifications.notification AS notification
            """;
        await db.Database.ExecuteSqlRawAsync(seedAttempts);
        await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.notification");
        await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.notification_attempt");
    }

    private static async Task RecreateOverdueIndexAsync(NotificationsDbContext db)
        => await db.Database.ExecuteSqlRawAsync(
            $"""
            CREATE INDEX {OverdueIndex} ON notifications.notification_attempt (status, fallback_deadline)
            WHERE fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL
              AND fallback_requested_at IS NULL
            """);

    private static async Task RecreateUnknownIndexAsync(NotificationsDbContext db)
        => await db.Database.ExecuteSqlRawAsync(
            $"""
            CREATE INDEX {UnknownIndex} ON notifications.notification_attempt (status_changed_at)
            WHERE status = 'unknown' AND fallback_deadline IS NOT NULL
              AND plan_advanced_at IS NULL AND fallback_requested_at IS NULL
            """);

    private static async Task RecreateReleaseIndexAsync(NotificationsDbContext db)
        => await db.Database.ExecuteSqlRawAsync(
            $"""
            CREATE INDEX {ReleaseIndex} ON notifications.notification (release_at)
            WHERE status = 'deferred'
            """);
}
