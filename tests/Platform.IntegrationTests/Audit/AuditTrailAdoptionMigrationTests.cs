using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// Exercises the conversion and adoption path over a database that already
/// holds audit events: the plain table is migrated to the partitioned layout
/// by the origin module's history, then the Audit module adopts it. The shared
/// fixture cannot cover this path because it always migrates an empty database
/// from scratch.
/// </summary>
public sealed class AuditTrailAdoptionMigrationTests : IAsyncLifetime
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
    public async Task Adopting_a_populated_audit_table_keeps_events_unchained_and_anchors_the_first_chained_append()
    {
        await using TemplateManagementDbContext templateDb = CreateTemplateManagementContext();
        IMigrator migrator = templateDb.GetService<IMigrator>();
        await migrator.MigrateAsync("ClassPolicyGovernance");

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DateTimeOffset previousMonth = new DateTimeOffset(
            now.Year, now.Month, 1, 12, 0, 0, TimeSpan.Zero).AddMonths(-1);
        var currentMonthEvent = Guid.CreateVersion7();
        var previousMonthEvent = Guid.CreateVersion7();
        await InsertPreChainAuditEventAsync(templateDb, currentMonthEvent, now, "conversion-current");
        await InsertPreChainAuditEventAsync(templateDb, previousMonthEvent, previousMonth, "conversion-previous");

        await templateDb.Database.MigrateAsync();
        await using AuditDbContext auditDb = CreateAuditContext();
        await auditDb.Database.MigrateAsync();

        // Every pre-existing event stays readable through the adopting model,
        // with its identity and its store-assigned sequence untouched, and no
        // chain data fabricated retroactively.
        List<AuditEvent> events = await auditDb.AuditEvents.AsNoTracking()
            .OrderBy(auditEvent => auditEvent.Seq)
            .ToListAsync();
        events.Count.ShouldBe(2);
        events[0].Id.ShouldBe(currentMonthEvent);
        events[0].Seq.ShouldBe(1);
        events[1].Id.ShouldBe(previousMonthEvent);
        events[1].Seq.ShouldBe(2);
        events.ShouldAllBe(auditEvent => auditEvent.Canonical == null
            && auditEvent.PrevHash == null
            && auditEvent.Hash == null);

        // Each event sits in the audit-schema partition of its occurrence
        // month, including the month older than the conversion month.
        (await PartitionHoldingAsync(auditDb, "conversion-current"))
            .ShouldBe($"audit.audit_event_{now.Year:D4}_{now.Month:D2}");
        (await PartitionHoldingAsync(auditDb, "conversion-previous"))
            .ShouldBe($"audit.audit_event_{previousMonth.Year:D4}_{previousMonth.Month:D2}");

        // The first chained append after the adoption: the sequence keeps
        // counting where it stopped, and with only unchained predecessors in
        // the partition the link starts at the documented partition anchor.
        await AppendThroughTrailAsync("conversion-after");
        AuditEvent chained = await auditDb.AuditEvents.AsNoTracking()
            .SingleAsync(auditEvent => auditEvent.EntityId == "conversion-after");
        chained.Seq.ShouldBe(3);
        var partitionName = $"audit_event_{now.Year:D4}_{now.Month:D2}";
        var anchor = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"notification-hub:audit-chain:{partitionName}:anchor"));
        chained.PrevHash.ShouldBe(anchor);
        chained.Hash.ShouldBe(SHA256.HashData(
            [.. anchor, .. Encoding.UTF8.GetBytes(chained.Canonical!)]));

        // The append-only trigger survives conversion and adoption on the
        // partitioned parent.
        PostgresException exception = await Should.ThrowAsync<PostgresException>(
            () => auditDb.Database.ExecuteSqlAsync(
                $"UPDATE audit.audit_event SET actor_id = 'tampered' WHERE entity_id = 'conversion-current'"));
        exception.Message.ShouldContain("append-only");
    }

    private TemplateManagementDbContext CreateTemplateManagementContext()
    {
        DbContextOptions<TemplateManagementDbContext> options =
            new DbContextOptionsBuilder<TemplateManagementDbContext>()
                .UseNpgsql(_postgres.GetConnectionString(), npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "templatemanagement"))
                .Options;
        return new TemplateManagementDbContext(options);
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

    private async Task AppendThroughTrailAsync(string entityId)
    {
        var trail = new TransactionalAuditTrail();
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await trail.AppendAsync(transaction, new AuditEntry
        {
            ActorType = AuditActorTypes.User,
            ActorId = "author-conversion",
            Action = AuditActions.TemplateCreated,
            EntityType = AuditEntityTypes.Template,
            EntityId = entityId,
            DetailsJson = "{}",
            OccurredAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static Task<int> InsertPreChainAuditEventAsync(
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

    private static async Task<string> PartitionHoldingAsync(AuditDbContext db, string entityId)
        => await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT tableoid::regclass::text AS "Value"
                 FROM audit.audit_event
                 WHERE entity_id = {entityId}
                 """)
            .SingleAsync();
}
