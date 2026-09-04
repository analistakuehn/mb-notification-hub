using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Migrations;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Notifications.Reconciliation;

/// <summary>
/// What the planner does with the statement the reconciliation actually sends.
/// <para>
/// This job is the correction of last resort, and its whole justification is
/// that it costs almost nothing: it runs once a day over a bounded batch of
/// attempts nobody ever answered about. That justification is arithmetic, and
/// it rests on a partial index the statement has to imply. Nothing about the
/// failure is visible from the outside, which is exactly why it went unnoticed:
/// the round returns the same rows either way, the functional tests stay green,
/// and the job silently becomes a sequential walk of every partition of
/// <c>notification_attempt</c> with an external sort on top of it.
/// </para>
/// <para>
/// The plan is read against the statement the code composes, never a copy, and
/// every assertion is proved falsifiable in the test that makes it: the index
/// is dropped and the degraded plan is read back. An oracle that cannot fail is
/// a sentence, not a measurement.
/// </para>
/// </summary>
[Collection(QueryPlanCollectionDefinition.Name)]
public sealed class ReconciliationCandidatePlanTests : IAsyncLifetime
{
    /// <summary>
    /// Enough rows that a walk is never the cheaper plan, in the mixture
    /// production has: almost everything settled, a small minority still parked
    /// with a provider that never reported back.
    /// </summary>
    private const int SeededAttempts = 40_000;

    private const string AttemptTable = "notification_attempt";
    private const string CandidateIndex = "ix_notification_attempt_reconciliation_due";

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
    public async Task The_planner_answers_the_candidate_selection_with_the_index_and_walks_the_table_without_it()
    {
        CapturedCommand captured = await CaptureAsync();

        IReadOnlyList<string> withIndex = await ExplainAsync(captured);
        await ShouldUseAsync(withIndex, CandidateIndex);

        // The age is answered by the index rather than filtered after it, which
        // is the difference between reading the parked attempts and reading
        // every attempt ever sent in order to discard the recent ones.
        withIndex.ShouldContain(
            line => line.Contains("Index Cond", StringComparison.Ordinal)
                && line.Contains("COALESCE", StringComparison.Ordinal),
            Plan(withIndex));

        await using (NotificationsDbContext db = CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync($"DROP INDEX notifications.{CandidateIndex}");
        }

        IReadOnlyList<string> withoutIndex = await ExplainAsync(captured);
        await ShouldWalkAsync(withoutIndex);

        await using (NotificationsDbContext db = CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                InitialSchema.ReconciliationDueIndexSql);
        }

        await ShouldUseAsync(await ExplainAsync(captured), CandidateIndex);
    }

    /// <summary>
    /// The statement carries the creation window that lets the planner discard
    /// the partitions a notification cannot have attempts in. Without it the
    /// join reads every partition of the notification table, and that is the
    /// half of the cost the index on the attempts does nothing about.
    /// </summary>
    [RequiresDockerFact]
    public async Task The_candidate_selection_bounds_the_join_by_the_partition_key()
    {
        CapturedCommand captured = await CaptureAsync();

        captured.CommandText.Contains("created_at", StringComparison.Ordinal).ShouldBeTrue(
            "a janela de criação é o que permite ao planner descartar partições; sem ela a junção "
            + "lê a tabela inteira de notificações.");

        IReadOnlyList<string> plan = await ExplainAsync(captured);
        plan.ShouldContain(
            line => line.Contains("Index", StringComparison.Ordinal),
            Plan(plan));
    }

    private async Task ShouldUseAsync(IReadOnlyList<string> plan, string parentIndex)
    {
        IReadOnlyList<string> children = await ChildIndexesOfAsync(parentIndex);
        children.ShouldNotBeEmpty($"o índice '{parentIndex}' não tem índices de partição.");
        plan.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && children.Any(child => line.Contains(child, StringComparison.Ordinal)),
            $"o plano não usou nenhum índice filho de '{parentIndex}':{Environment.NewLine}{Plan(plan)}");

        IReadOnlyList<string> populated = await PopulatedPartitionsOfAsync(AttemptTable);
        populated.ShouldNotBeEmpty(
            $"nenhuma partição de '{AttemptTable}' tem linhas; o plano estaria medindo o tamanho da "
            + "tabela e não o índice.");
        plan.ShouldNotContain(line => WalksAny(line, populated), Plan(plan));
    }

    private async Task ShouldWalkAsync(IReadOnlyList<string> plan)
    {
        IReadOnlyList<string> populated = await PopulatedPartitionsOfAsync(AttemptTable);
        plan.ShouldContain(line => WalksAny(line, populated), Plan(plan));
    }

    private static bool WalksAny(string line, IReadOnlyList<string> partitions)
        => line.Contains("Seq Scan", StringComparison.Ordinal)
            && partitions.Any(partition => line.Contains(partition, StringComparison.Ordinal));

    /// <summary>
    /// One command exactly as the query pipeline built it: the text and every
    /// bound value. Rebuilding it by hand is the thing this test exists not to
    /// do, because a plan read over a transcription grades the transcription.
    /// </summary>
    private sealed record CapturedCommand(
        string CommandText,
        IReadOnlyList<KeyValuePair<string, object?>> Parameters);

    private async Task<CapturedCommand> CaptureAsync()
    {
        var interceptor = new CommandCapture();
        await using NotificationsDbContext db = CreateContext(interceptor);

        // Executed rather than merely composed: the pipeline only produces the
        // command when it runs, and the answer it returns is irrelevant here.
        await DeliveryReconciliationScan
            .CandidateQuery(
                db,
                DateTimeOffset.UtcNow.AddHours(-6),
                ["sendgrid", "twilio"],
                200)
            .ToListAsync();

        return interceptor.Captured.ShouldNotBeNull(
            "nenhum comando foi capturado; sem ele o teste não mede o statement real.");
    }

    private async Task<IReadOnlyList<string>> ExplainAsync(CapturedCommand captured)
    {
        await using NotificationsDbContext db = CreateContext();
        await db.Database.OpenConnectionAsync();
        try
        {
            var lines = new List<string>();
            await using DbCommand command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "EXPLAIN " + captured.CommandText;
            foreach (KeyValuePair<string, object?> parameter in captured.Parameters)
            {
                DbParameter bound = command.CreateParameter();
                bound.ParameterName = parameter.Key;
                bound.Value = parameter.Value ?? DBNull.Value;
                command.Parameters.Add(bound);
            }

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

    private async Task<IReadOnlyList<string>> PopulatedPartitionsOfAsync(string table)
    {
        await using NotificationsDbContext db = CreateContext();
        return await db.Database
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
    }

    private async Task<IReadOnlyList<string>> ChildIndexesOfAsync(string parentIndex)
    {
        await using NotificationsDbContext db = CreateContext();
        return await db.Database
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
    }

    private static string Plan(IReadOnlyList<string> lines)
        => string.Join(Environment.NewLine, lines);

    private NotificationsDbContext CreateContext(CommandCapture? interceptor = null)
    {
        DbContextOptionsBuilder<NotificationsDbContext> builder =
            new DbContextOptionsBuilder<NotificationsDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"));
        if (interceptor is not null) builder.AddInterceptors(interceptor);

        return new NotificationsDbContext(builder.Options);
    }

    private sealed class CommandCapture : DbCommandInterceptor
    {
        public CapturedCommand? Captured { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Capture(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Capture(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Capture(DbCommand command)
            => Captured ??= new CapturedCommand(
                command.CommandText,
                [.. command.Parameters
                    .Cast<DbParameter>()
                    .Select(parameter => new KeyValuePair<string, object?>(
                        parameter.ParameterName,
                        parameter.Value))]);
    }

    /// <summary>
    /// A backlog in the shape production has: almost every attempt settled, a
    /// small minority still parked with a provider that never reported, and a
    /// share of those on a channel with no lookup at all, which is the rows the
    /// selection has to leave out.
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
                  'app-recon', 'idem-' || n, 'cus_' || n,
                  'transactional', 'tpl-recon', false, 1, 1, jsonb_build_object(), NULL, NULL,
                  'recon-plan-tests', 'dispatched', NULL, now() + interval '1 day'
              FROM generate_series(1, {{SeededAttempts}}) AS n
              """);
        await db.Database.ExecuteSqlRawAsync(seedNotifications);

        const string seedAttempts = """
            INSERT INTO notifications.notification_attempt
                (id, created_at, notification_id, sequence, channel, provider_key, contact_point_id,
                 device_token_id, provider_message_id, rendered_content_enc, content_hash_full,
                 content_hash_masked, status, error_code, fallback_deadline, plan_advanced_at,
                 status_changed_at, fallback_requested_at, sent_at, delivered_at)
            SELECT
                gen_random_uuid(), notification.created_at, notification.id, 1,
                CASE WHEN notification.row_number % 3 = 0 THEN 'push' ELSE 'email' END,
                CASE WHEN notification.row_number % 3 = 0 THEN 'fcm' ELSE 'sendgrid' END,
                NULL, NULL, 'msg-' || notification.row_number,
                '\x01'::bytea, repeat('a', 64), repeat('a', 64),
                CASE
                    WHEN notification.row_number % 200 = 0 THEN 'sent'
                    WHEN notification.row_number % 401 = 0 THEN 'unknown'
                    ELSE 'delivered'
                END,
                NULL, NULL, NULL, notification.created_at, NULL, notification.created_at, NULL
            FROM (
                SELECT id, created_at, row_number() OVER (ORDER BY created_at) AS row_number
                FROM notifications.notification
                WHERE requested_by = 'recon-plan-tests'
            ) AS notification
            """;
        await db.Database.ExecuteSqlRawAsync(seedAttempts);
        await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.notification_attempt");
        await db.Database.ExecuteSqlRawAsync("ANALYZE notifications.notification");
    }
}
