using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.TemplateManagement;

/// <summary>
/// Exercises the conversion path of the partitioning migration: a database
/// that already holds audit events on the plain table, migrated forward to the
/// partitioned layout. The shared fixture cannot cover this path because it
/// always migrates an empty database from scratch.
/// </summary>
public sealed class AuditEventPartitionMigrationTests : IAsyncLifetime
{
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

    async Task IAsyncLifetime.DisposeAsync()
        => await _postgres.DisposeAsync();

    [RequiresDockerFact]
    public async Task Converting_a_populated_audit_table_keeps_every_event_readable_in_its_month_partition()
    {
        await using TemplateManagementDbContext db = CreateContext();
        IMigrator migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("ClassPolicyGovernance");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset previousMonth = new DateTimeOffset(
            now.Year, now.Month, 1, 12, 0, 0, TimeSpan.Zero).AddMonths(-1);
        var currentMonthEvent = Guid.CreateVersion7();
        var previousMonthEvent = Guid.CreateVersion7();
        await InsertAuditEventAsync(db, currentMonthEvent, now, "conversion-current");
        await InsertAuditEventAsync(db, previousMonthEvent, previousMonth, "conversion-previous");

        await db.Database.MigrateAsync();

        // Every pre-existing event stays readable through the model, with its
        // identity and its store-assigned sequence untouched.
        List<AuditEvent> events = await db.AuditEvents.AsNoTracking()
            .OrderBy(auditEvent => auditEvent.Seq)
            .ToListAsync();
        events.Count.ShouldBe(2);
        events[0].Id.ShouldBe(currentMonthEvent);
        events[0].Seq.ShouldBe(1);
        events[1].Id.ShouldBe(previousMonthEvent);
        events[1].Seq.ShouldBe(2);

        // Each event sits in the partition of its occurrence month, including
        // the month older than the conversion month.
        (await PartitionHoldingAsync(db, "conversion-current"))
            .ShouldBe($"templatemanagement.audit_event_{now.Year:D4}_{now.Month:D2}");
        (await PartitionHoldingAsync(db, "conversion-previous"))
            .ShouldBe($"templatemanagement.audit_event_{previousMonth.Year:D4}_{previousMonth.Month:D2}");

        // The sequence keeps counting where it stopped: the next insert takes
        // the next value instead of restarting from one.
        AuditEvent afterConversion = AuditEvent.Record(new AuditEntry
        {
            ActorType = AuditActorTypes.User,
            ActorId = "author-conversion",
            Action = AuditActions.TemplateCreated,
            EntityType = AuditEntityTypes.Template,
            EntityId = "conversion-after",
            DetailsJson = "{}",
            OccurredAt = DateTimeOffset.UtcNow,
        });
        db.AuditEvents.Add(afterConversion);
        await db.SaveChangesAsync();
        afterConversion.Seq.ShouldBe(3);

        // The append-only trigger survives the conversion on the new parent.
        PostgresException exception = await Should.ThrowAsync<PostgresException>(
            () => db.Database.ExecuteSqlAsync(
                $"UPDATE templatemanagement.audit_event SET actor_id = 'tampered' WHERE entity_id = 'conversion-current'"));
        exception.Message.ShouldContain("append-only");
    }

    private TemplateManagementDbContext CreateContext()
    {
        DbContextOptions<TemplateManagementDbContext> options =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "templatemanagement"))
                .Options;
        return new TemplateManagementDbContext(options);
    }

    private static Task<int> InsertAuditEventAsync(
        TemplateManagementDbContext db,
        Guid id,
        DateTimeOffset occurredAt,
        string entityId)
        => db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO templatemanagement.audit_event
                 (id, occurred_at, actor_type, actor_id, application, action, entity_type, entity_id, details)
             VALUES ({id}, {occurredAt}, 'user', 'author-conversion', NULL, 'template.created', 'template', {entityId}, {"{}"}::jsonb)
             """);

    private static async Task<string> PartitionHoldingAsync(TemplateManagementDbContext db, string entityId)
        => await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT tableoid::regclass::text AS "Value"
                 FROM templatemanagement.audit_event
                 WHERE entity_id = {entityId}
                 """)
            .SingleAsync();
}
