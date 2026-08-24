using System.Data.Common;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Migrations;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Events;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using Npgsql;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Messaging;

/// <summary>
/// The band the relay claims by is stored, not derived, and three questions
/// follow: whether the planner answers the claim with the index, whether the
/// expression the database computes agrees with the classification the reader
/// applies, and whether a row written before the column existed comes out of
/// the migration in the right band. The database is this class's own because
/// two of the answers change the schema under the claim, and a plan read on an
/// almost empty table would grade the size of the table instead of the index.
/// </summary>
public sealed class OutboxPriorityBandTests : IAsyncLifetime
{
    private const int SeededRows = 30_000;

    private const string DropPendingIndexSql = "DROP INDEX platform.ix_outbox_pending";

    private const string PendingIndexName = "ix_outbox_pending";

    private const string UnknownPriorityClass = "misspelled";

    /// <summary>
    /// The insert as the writer shaped it before the band column existed: the
    /// column list names every column of the row and none of them is the band.
    /// </summary>
    private const string PreBandInsertSql = """
        INSERT INTO platform.outbox
            (id, destination, transport, event_type, message_key, headers, payload,
             priority_class, created_at, sent_at)
        VALUES
            (@id, @destination, 'sqs', 'notification.accepted', @messageKey, '{}'::jsonb,
             '{}'::jsonb, @priorityClass, @createdAt, NULL)
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
    public async Task The_planner_answers_the_claim_with_the_index_and_scans_the_table_without_it()
    {
        await using PlatformMessagingDbContext db = CreateContext();
        await db.Database.MigrateAsync();
        await SeedPendingBacklogAsync(db);

        IReadOnlyList<string> withIndex = await ExplainClaimAsync(db, OutboxBand.Auth);

        // The plan is the oracle, not the presence of the index: the band used
        // to be an expression in this predicate, and an expression is what no
        // index can answer. The sort is half the claim, because the batch has to
        // come out in arrival order, and ordering the survivors of a scan spills
        // to disk as soon as the backlog grows.
        withIndex.ShouldContain(
            line => line.Contains("Index Scan", StringComparison.Ordinal)
                && line.Contains(PendingIndexName, StringComparison.Ordinal),
            Plan(withIndex));
        withIndex.ShouldNotContain(line => line.Contains("Seq Scan", StringComparison.Ordinal), Plan(withIndex));
        withIndex.ShouldNotContain(line => line.Contains("Sort", StringComparison.Ordinal), Plan(withIndex));

        await db.Database.ExecuteSqlRawAsync(DropPendingIndexSql);
        IReadOnlyList<string> withoutIndex = await ExplainClaimAsync(db, OutboxBand.Auth);

        // The same claim without the index is the state this change corrects,
        // and measuring it here is what keeps the assertions above from being
        // sentences that pass on any schema at all.
        withoutIndex.ShouldContain(
            line => line.Contains("Seq Scan", StringComparison.Ordinal), Plan(withoutIndex));
        withoutIndex.ShouldContain(
            line => line.Contains("Sort", StringComparison.Ordinal), Plan(withoutIndex));
    }

    [RequiresDockerFact]
    public async Task The_stored_band_agrees_with_the_classification_of_the_reader_on_every_known_route()
    {
        await using PlatformMessagingDbContext db = CreateContext();
        await db.Database.MigrateAsync();

        List<(string Destination, string PriorityClass)> routes = [.. KnownRoutes()];
        var writer = new TransactionalOutboxWriter(TimeProvider.System);
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        foreach ((var destination, var priorityClass) in routes)
        {
            await using DbTransaction transaction = await connection.BeginTransactionAsync();
            await writer.AppendAsync(
                transaction, OutboxEnvelopes.Envelope(destination, priorityClass), CancellationToken.None);
            await transaction.CommitAsync();
        }

        // One row per pair, or the comparison below would be silently confronting
        // fewer routes than the product has. An empty set agrees with everything.
        (await db.OutboxMessages.CountAsync()).ShouldBe(routes.Count);
        IReadOnlyList<string> disagreements = await DisagreementsWithTheReaderAsync(db);

        // Two implementations of one rule, one in C# and one in the column the
        // database computes. Confronting them is the only thing that keeps them
        // from drifting apart in silence, and the rule that would drift first is
        // the one the plan cannot see: an authentication destination belongs to
        // the top band whatever class the producer stored on the row.
        disagreements.ShouldBeEmpty(string.Join(Environment.NewLine, disagreements));
    }

    [RequiresDockerFact]
    public async Task Rows_written_before_the_band_existed_leave_the_migration_in_their_band()
    {
        await using PlatformMessagingDbContext db = CreateContext();
        await MigrateToTheVersionBeforeTheBandAsync(db);

        // Without this the test would still pass against a schema that already
        // had the column, and it would be proving nothing about the migration:
        // the insert below omits the band either way.
        (await BandColumnsAsync(db)).ShouldBe(0);

        (string Destination, string PriorityClass)[] rows =
        [
            ("core-auth", "transactional"),
            ("core-critical", "critical"),
            ("core-transactional", "transactional"),
            ("core-operational", "operational"),
            ("dispatch-push-auth", "critical"),
            ("dispatch-email-critical", "critical"),
            ("contacts-changed", UnknownPriorityClass),
        ];
        foreach ((var destination, var priorityClass) in rows)
        {
            await InsertWithoutBandAsync(db, destination, priorityClass);
        }

        await db.Database.MigrateAsync();

        (await BandColumnsAsync(db)).ShouldBe(1);
        IReadOnlyList<string> disagreements = await DisagreementsWithTheReaderAsync(db);
        (await db.OutboxMessages.CountAsync()).ShouldBe(rows.Length);
        disagreements.ShouldBeEmpty(string.Join(Environment.NewLine, disagreements));
    }

    /// <summary>How many band columns the outbox has right now: none before the migration, one after.</summary>
    private static async Task<int> BandColumnsAsync(PlatformMessagingDbContext db)
    {
        await db.Database.OpenConnectionAsync();
        DbConnection connection = db.Database.GetDbConnection();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT count(*)
                FROM information_schema.columns
                WHERE table_schema = 'platform'
                  AND table_name = 'outbox'
                  AND column_name = 'priority_band'
                """;
            var count = Convert.ToInt32(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            await db.Database.CloseConnectionAsync();
            return count;
        }
    }

    /// <summary>
    /// The full cartesian product of destination by stored class. The domain is
    /// small and closed, so the confrontation between the two implementations of
    /// the band rule enumerates it instead of sampling it.
    ///
    /// Every family is derived from the vocabulary production already owns: the
    /// core queues from the composition of the worker that consumes them, the
    /// dispatch queues from the routing function the pipeline itself calls over
    /// the canonical channels and classes, and the two event destinations from
    /// the constants their owners publish. A channel, a class or a core queue
    /// added tomorrow enters this test on its own, which is the property a
    /// hand-written list cannot have.
    /// </summary>
    private static IEnumerable<(string Destination, string PriorityClass)> KnownRoutes()
    {
        IReadOnlyList<string> destinations =
        [
            .. CoreWorkerRole.BandQueues.Select(entry => entry.Queue),
            .. from channel in Channel.All
               select RouteStage.DestinationFor(
                   ResolveStage.AuthenticationPurpose, channel.Value, NotificationClasses.Critical),
            .. from channel in Channel.All
               from canonicalClass in NotificationClasses.CanonicalValues
               select RouteStage.DestinationFor(null, channel.Value, canonicalClass),
            ContactConsentEvents.Destination,
            OutgoingEventBus.Topic,

            // No queue answers to these two. They are here because the rule of
            // the top band has two halves, and a rewrite that keeps only the
            // suffix or only the prefix would still classify every real route
            // correctly while pulling ordinary traffic into the band that
            // protects authentication.
            "contacts-auth",
            "core-auth-events",
        ];

        IReadOnlyList<string> storedClasses = [.. NotificationClasses.CanonicalValues, UnknownPriorityClass];

        return from destination in destinations
               from priorityClass in storedClasses
               select (destination, priorityClass);
    }

    /// <summary>
    /// Every stored row whose band is not the band the reader would pick for it,
    /// described well enough to act on without opening the database.
    /// </summary>
    private static async Task<IReadOnlyList<string>> DisagreementsWithTheReaderAsync(
        PlatformMessagingDbContext db)
    {
        var stored = await db.OutboxMessages
            .AsNoTracking()
            .Select(message => new
            {
                message.Destination,
                message.PriorityClass,
                message.PriorityBand,
            })
            .ToListAsync();

        return
        [
            .. from row in stored
               let expected = (int)OutboxBands.Classify(row.Destination, row.PriorityClass)
               where row.PriorityBand != expected
               select string.Create(
                   CultureInfo.InvariantCulture,
                   $"{row.Destination} com classe {row.PriorityClass}: "
                   + $"coluna {row.PriorityBand}, leitor {expected}"),
        ];
    }

    private PlatformMessagingDbContext CreateContext()
    {
        DbContextOptions<PlatformMessagingDbContext> options =
            new DbContextOptionsBuilder<PlatformMessagingDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"))
                .Options;
        return new PlatformMessagingDbContext(options);
    }

    /// <summary>
    /// Stops the history one migration short of the band, found by position
    /// instead of by name so a later migration never turns this into a test of
    /// a schema nobody runs anymore.
    /// </summary>
    private static async Task MigrateToTheVersionBeforeTheBandAsync(PlatformMessagingDbContext db)
    {
        List<string> history = [.. db.Database.GetMigrations()];
        var band = history.FindIndex(
            migration => migration.EndsWith(nameof(AddOutboxPriorityBand), StringComparison.Ordinal));
        band.ShouldBeGreaterThan(0);
        await db.Database.GetService<IMigrator>().MigrateAsync(history[band - 1]);
    }

    private static async Task InsertWithoutBandAsync(
        PlatformMessagingDbContext db, string destination, string priorityClass)
    {
        await db.Database.OpenConnectionAsync();
        DbConnection connection = db.Database.GetDbConnection();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = PreBandInsertSql;
            AddParameter(command, "id", Guid.CreateVersion7());
            AddParameter(command, "destination", destination);
            AddParameter(command, "messageKey", $"cus_{Guid.NewGuid():N}");
            AddParameter(command, "priorityClass", priorityClass);
            AddParameter(command, "createdAt", DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync();
        }

        await db.Database.CloseConnectionAsync();
    }

    /// <summary>
    /// Pending rows in the mixture the producers write, with the auth band rare
    /// on purpose: a rare band is what separates an index scan from a walk,
    /// because a walk has to pass every row of every other band to fill a batch.
    /// </summary>
    private static async Task SeedPendingBacklogAsync(PlatformMessagingDbContext db)
    {
        var seed = string.Create(
            CultureInfo.InvariantCulture,
            $$"""
              INSERT INTO platform.outbox
                  (id, destination, transport, event_type, message_key, headers, payload,
                   priority_class, created_at, sent_at)
              SELECT
                  gen_random_uuid(),
                  CASE WHEN n % 50 = 0 THEN 'core-auth'
                       WHEN n % 5 = 0 THEN 'core-critical'
                       WHEN n % 5 = 1 THEN 'core-operational'
                       ELSE 'core-transactional' END,
                  'sqs', 'notification.accepted', 'cus_' || n,
                  jsonb_build_object(), jsonb_build_object(),
                  CASE WHEN n % 5 = 0 THEN 'critical'
                       WHEN n % 5 = 1 THEN 'operational'
                       ELSE 'transactional' END,
                  now() - (n * interval '1 millisecond'),
                  NULL
              FROM generate_series(1, {{SeededRows}}) AS n
              """);
        await db.Database.ExecuteSqlRawAsync(seed);
        await db.Database.ExecuteSqlRawAsync("ANALYZE platform.outbox");
    }

    private static async Task<IReadOnlyList<string>> ExplainClaimAsync(
        PlatformMessagingDbContext db, OutboxBand band)
    {
        await db.Database.OpenConnectionAsync();
        DbConnection connection = db.Database.GetDbConnection();
        var lines = new List<string>();
        await using (DbCommand command = connection.CreateCommand())
        {
            command.CommandText = "EXPLAIN " + PostgresOutboxPendingStore.ClaimSql;
            AddParameter(command, "transport", OutboxTransports.Sqs);
            AddParameter(command, "band", (int)band);
            AddParameter(command, "batchSize", 100);
            await using DbDataReader reader = await command.ExecuteReaderAsync();
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
}
