using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Retention;
using NotificationHub.IntegrationTests.Notifications;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.AttachmentManagement;

/// <summary>
/// What the planner does with the statements the two maintenance rounds
/// actually send: the repair of outstanding liabilities, and the sweep of
/// attachments whose content has been abandoned.
/// <para>
/// The round is justified by arithmetic: almost no attachment owes a repair,
/// so reading the ones that do costs the size of the backlog and not the size
/// of the table. That arithmetic rests entirely on the partial index, and
/// nothing about it failing is visible from outside. The round returns the same
/// rows either way, every functional test stays green, and the job quietly
/// becomes a walk of every attachment ever registered with a sort on top.
/// </para>
/// <para>
/// The plan is read against the statement the code composes, never a copy of
/// it, and the assertion is proved falsifiable in the same test: the index is
/// dropped, the degraded plan is read back, and the index is put back from the
/// definition the catalog itself reported.
/// </para>
/// </summary>
[Collection(QueryPlanCollectionDefinition.Name)]
public sealed class AttachmentLiabilityPlanTests : IAsyncLifetime
{
    /// <summary>
    /// Enough rows that a walk is never the cheaper plan, in the mixture
    /// production has: almost every attachment owing nothing, a small minority
    /// carrying an outstanding repair.
    /// </summary>
    private const int SeededAttachments = 40_000;

    private const string AttachmentTable = "attachment";
    private const string LiabilityIndex = "ix_attachment_reconciliation_liability";
    private const string AbandonmentIndex = "ix_attachment_abandonment";

    /// <summary>
    /// Windows that leave a small minority of the seeded rows abandoned, which
    /// is the mixture the sweep meets in production: the backlog is whatever
    /// crossed its deadline since the last round, and never the table.
    /// </summary>
    private static readonly AttachmentRetentionWindows Windows = new(
        UnstartedUpload: TimeSpan.FromHours(11),
        UnvalidatedContent: TimeSpan.FromHours(11),
        RefusedContent: TimeSpan.FromHours(11),
        WithdrawnRelease: TimeSpan.FromHours(11));

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
        await using AttachmentManagementDbContext db = CreateContext();
        await db.Database.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
        await SeedAsync(db);
    }

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    [RequiresDockerFact]
    public async Task The_planner_answers_the_outstanding_selection_with_the_partial_index()
    {
        CapturedCommand captured = await CaptureAsync();

        IReadOnlyList<string> withIndex = await ExplainAsync(captured);
        withIndex.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && line.Contains(LiabilityIndex, StringComparison.Ordinal),
            Plan(withIndex));

        // The order is answered by the index and not by a sort on top of it,
        // which is the difference between reading the backlog and reading every
        // attachment in order to put the backlog in order.
        withIndex.ShouldNotContain(
            line => line.Contains("Sort", StringComparison.Ordinal),
            Plan(withIndex));

        withIndex.ShouldNotContain(
            line => line.Contains("Seq Scan", StringComparison.Ordinal)
                && line.Contains(AttachmentTable, StringComparison.Ordinal),
            Plan(withIndex));
    }

    /// <summary>
    /// The assertion above with its floor measured: without the index the same
    /// statement walks the table, so the green one is reporting the index and
    /// not the shape of a query that would be cheap either way.
    /// </summary>
    [RequiresDockerFact]
    public async Task Without_the_partial_index_the_same_statement_walks_the_table()
    {
        CapturedCommand captured = await CaptureAsync();
        var definition = await IndexDefinitionAsync(LiabilityIndex);
        definition.ShouldNotBeNullOrWhiteSpace(
            $"o índice '{LiabilityIndex}' não existe, portanto o teste mediria a ausência dele "
            + "em vez do plano.");

        // The filter is read off the catalog rather than off the model, because
        // what has to be partial is the structure the database built.
        definition.ShouldContain("reconciliation_liability IS NOT NULL", Case.Sensitive);

        await ExecuteAsync($"DROP INDEX attachmentmanagement.{LiabilityIndex}");
        try
        {
            IReadOnlyList<string> withoutIndex = await ExplainAsync(captured);
            withoutIndex.ShouldContain(
                line => line.Contains("Seq Scan", StringComparison.Ordinal)
                    && line.Contains(AttachmentTable, StringComparison.Ordinal),
                Plan(withoutIndex));
        }
        finally
        {
            await ExecuteAsync(definition);
        }

        IReadOnlyList<string> restored = await ExplainAsync(captured);
        restored.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && line.Contains(LiabilityIndex, StringComparison.Ordinal),
            Plan(restored));
    }

    /// <summary>
    /// The sweep of abandoned content reads the same kind of structure, for
    /// the same reason: what it has to reach is whatever crossed a deadline,
    /// and the filter is what keeps an attachment out of the index for good
    /// once its content is gone or released.
    /// <para>
    /// The order is answered by the index too. Without that, draining a
    /// backlog oldest first would mean sorting every attachment that can still
    /// be abandoned in order to take a hundred of them.
    /// </para>
    /// </summary>
    [RequiresDockerFact]
    public async Task The_planner_answers_the_abandonment_selection_with_the_partial_index()
    {
        CapturedCommand captured = await CaptureAbandonmentAsync();

        IReadOnlyList<string> withIndex = await ExplainAsync(captured);
        withIndex.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && line.Contains(AbandonmentIndex, StringComparison.Ordinal),
            Plan(withIndex));
        withIndex.ShouldNotContain(
            line => line.Contains("Sort", StringComparison.Ordinal),
            Plan(withIndex));
        withIndex.ShouldNotContain(
            line => line.Contains("Seq Scan", StringComparison.Ordinal)
                && line.Contains(AttachmentTable, StringComparison.Ordinal),
            Plan(withIndex));
    }

    /// <summary>
    /// The assertion above with its floor measured: without the index the same
    /// statement walks the table, so the green one is reporting the index and
    /// not the shape of a query that would be cheap either way.
    /// </summary>
    [RequiresDockerFact]
    public async Task Without_the_partial_index_the_abandonment_selection_walks_the_table()
    {
        CapturedCommand captured = await CaptureAbandonmentAsync();
        var definition = await IndexDefinitionAsync(AbandonmentIndex);
        definition.ShouldNotBeNullOrWhiteSpace(
            $"o índice '{AbandonmentIndex}' não existe, portanto o teste mediria a ausência "
            + "dele em vez do plano.");

        // Read off the catalog rather than off the model, because what has to
        // be partial is the structure the database built, and because the
        // filter is the whole of what keeps a discarded attachment out of it.
        definition.ShouldContain("awaiting-upload", Case.Sensitive);
        definition.ShouldContain("revoked", Case.Sensitive);
        definition.ShouldNotContain("discarded", Case.Sensitive);
        definition.ShouldNotContain("released", Case.Sensitive);

        await ExecuteAsync($"DROP INDEX attachmentmanagement.{AbandonmentIndex}");
        try
        {
            IReadOnlyList<string> withoutIndex = await ExplainAsync(captured);
            withoutIndex.ShouldContain(
                line => line.Contains("Seq Scan", StringComparison.Ordinal)
                    && line.Contains(AttachmentTable, StringComparison.Ordinal),
                Plan(withoutIndex));
        }
        finally
        {
            await ExecuteAsync(definition);
        }

        IReadOnlyList<string> restored = await ExplainAsync(captured);
        restored.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && line.Contains(AbandonmentIndex, StringComparison.Ordinal),
            Plan(restored));
    }

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
        await using AttachmentManagementDbContext db = CreateContext(interceptor);

        // Executed rather than merely composed: the pipeline only produces the
        // command when it runs, and the answer it returns is irrelevant here.
        await AttachmentReconciliationScan
            .OutstandingQuery(db, DateTimeOffset.UtcNow, 100)
            .ToListAsync();

        return interceptor.Captured.ShouldNotBeNull(
            "nenhum comando foi capturado; sem ele o teste não mede o statement real.");
    }

    private async Task<CapturedCommand> CaptureAbandonmentAsync()
    {
        var interceptor = new CommandCapture();
        await using AttachmentManagementDbContext db = CreateContext(interceptor);

        await AttachmentAbandonmentScan
            .AbandonedQuery(db, DateTimeOffset.UtcNow, Windows, 100)
            .ToListAsync();

        return interceptor.Captured.ShouldNotBeNull(
            "nenhum comando foi capturado; sem ele o teste não mede o statement real.");
    }

    private async Task<IReadOnlyList<string>> ExplainAsync(CapturedCommand captured)
    {
        await using AttachmentManagementDbContext db = CreateContext();
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

    private async Task<string?> IndexDefinitionAsync(string indexName)
    {
        await using AttachmentManagementDbContext db = CreateContext();
        return await db.Database
            .SqlQuery<string?>(
                $"""
                SELECT indexdef AS "Value"
                FROM pg_indexes
                WHERE schemaname = 'attachmentmanagement' AND indexname = {indexName}
                """)
            .SingleOrDefaultAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using AttachmentManagementDbContext db = CreateContext();
        await db.Database.ExecuteSqlRawAsync(sql);
    }

    private static string Plan(IReadOnlyList<string> lines)
        => string.Join(Environment.NewLine, lines);

    private AttachmentManagementDbContext CreateContext(CommandCapture? interceptor = null)
    {
        DbContextOptionsBuilder<AttachmentManagementDbContext> builder =
            new DbContextOptionsBuilder<AttachmentManagementDbContext>()
                .UseNpgsql(_postgres.GetConnectionString());
        if (interceptor is not null) builder.AddInterceptors(interceptor);

        return new AttachmentManagementDbContext(builder.Options);
    }

    /// <summary>
    /// A backlog in the shape production has: forty thousand attachments that
    /// owe nothing, and a handful that do, split across both repairs so the
    /// selection has something of each kind to answer about.
    /// </summary>
    private static async Task SeedAsync(AttachmentManagementDbContext db)
    {
        var seed = string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              INSERT INTO attachmentmanagement.attachment
                  (id, reference, application, file_name, content_type, size_bytes, content_id,
                   state, created_at, received_at, validation_detail, inconclusive_until,
                   reconciliation_liability)
              SELECT
                  gen_random_uuid(),
                  'att_' || lpad(to_hex(n), 32, '0'),
                  'app-plan', 'file-' || n || '.pdf', 'application/pdf', 1024,
                  gen_random_uuid(),
                  CASE WHEN n % 5000 = 0 THEN '{{AttachmentStates.Inconclusive}}'
                       ELSE '{{AttachmentStates.Received}}' END,
                  now() - (n * interval '1 second'),
                  now() - (n * interval '1 second'),
                  NULL,
                  CASE WHEN n % 5000 = 0 THEN now() - interval '1 hour' ELSE NULL END,
                  CASE
                      WHEN n % 5000 = 0 THEN '{{AttachmentLiabilities.VerdictOpen}}'
                      WHEN n % 7000 = 0 THEN '{{AttachmentLiabilities.CustodyUnreclaimed}}'
                      ELSE NULL
                  END
              FROM generate_series(1, {{SeededAttachments}}) AS n
              """);
        await db.Database.ExecuteSqlRawAsync(seed);
        await db.Database.ExecuteSqlRawAsync("ANALYZE attachmentmanagement.attachment");
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
}
