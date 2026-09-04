using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;
using Testcontainers.PostgreSql;

namespace NotificationHub.IntegrationTests.Audit;

/// <summary>
/// A trail partition that already holds rows carrying no hash. The appender
/// reads the last chained event of the partition and skips rows without one,
/// so the first chained append after them links to the partition anchor
/// instead of to a predecessor that was never part of any chain. The shared
/// fixture cannot cover this: everything it writes is written through the
/// trail, and everything written through the trail is chained.
/// </summary>
public sealed class AuditTrailUnchainedEventTests : IAsyncLifetime
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
    public async Task An_event_without_a_hash_never_becomes_the_predecessor_of_a_chained_one()
    {
        // The audit history is applied on its own, over an empty database and
        // with no other context applied first: this module owns its tables and
        // borrows none.
        await using AuditDbContext auditDb = CreateAuditContext();
        await auditDb.Database.MigrateAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var firstUnchained = Guid.CreateVersion7();
        var secondUnchained = Guid.CreateVersion7();
        await InsertUnchainedAuditEventAsync(auditDb, firstUnchained, now, "unchained-first");
        await InsertUnchainedAuditEventAsync(auditDb, secondUnchained, now, "unchained-second");

        // Every unchained event stays readable through the model, with its
        // identity and its store-assigned sequence untouched, and no chain data
        // fabricated for it.
        List<AuditEvent> events = await auditDb.AuditEvents.AsNoTracking()
            .OrderBy(auditEvent => auditEvent.Seq)
            .ToListAsync();
        events.Count.ShouldBe(2);
        events[0].Id.ShouldBe(firstUnchained);
        events[0].Seq.ShouldBe(1);
        events[1].Id.ShouldBe(secondUnchained);
        events[1].Seq.ShouldBe(2);
        events.ShouldAllBe(auditEvent => auditEvent.Canonical == null
            && auditEvent.PrevHash == null
            && auditEvent.Hash == null);

        // The first chained append: the sequence keeps counting where the
        // unchained rows stopped, and the link starts at the documented
        // partition anchor rather than at the row immediately before it.
        await AppendThroughTrailAsync("chained-after-unchained");
        AuditEvent chained = await auditDb.AuditEvents.AsNoTracking()
            .SingleAsync(auditEvent => auditEvent.EntityId == "chained-after-unchained");
        chained.Seq.ShouldBe(3);
        var partitionName = $"audit_event_{now.Year:D4}_{now.Month:D2}";
        var anchor = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"notification-hub:audit-chain:{partitionName}:anchor"));
        chained.PrevHash.ShouldBe(anchor);
        chained.Hash.ShouldBe(SHA256.HashData(
            [.. anchor, .. Encoding.UTF8.GetBytes(chained.Canonical!)]));

        // An unchained row arriving after a chained one is the case the anchor
        // above cannot decide: with the row skipped, the next append links to
        // the last chained event; with the row taken as the predecessor, its
        // absent hash reads as no predecessor at all and the append anchors a
        // second time, which is a fork inside one partition.
        await InsertUnchainedAuditEventAsync(
            auditDb, Guid.CreateVersion7(), now, "unchained-after-chained");
        await AppendThroughTrailAsync("chained-over-the-unchained");
        AuditEvent linked = await auditDb.AuditEvents.AsNoTracking()
            .SingleAsync(auditEvent => auditEvent.EntityId == "chained-over-the-unchained");
        linked.Seq.ShouldBe(5);
        linked.PrevHash.ShouldBe(chained.Hash);
        linked.PrevHash.ShouldNotBe(anchor);
        linked.Hash.ShouldBe(SHA256.HashData(
            [.. linked.PrevHash!, .. Encoding.UTF8.GetBytes(linked.Canonical!)]));
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
            ActorId = "author-unchained",
            Action = AuditActions.TemplateCreated,
            EntityType = AuditEntityTypes.Template,
            EntityId = entityId,
            DetailsJson = "{}",
            OccurredAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Writes a row the way nothing in the product writes one: without the
    /// three chain columns. Their absence is what the appender has to skip
    /// over, and the check constraint admits all-or-none of them.
    /// </summary>
    private static Task<int> InsertUnchainedAuditEventAsync(
        AuditDbContext db,
        Guid id,
        DateTimeOffset occurredAt,
        string entityId)
        => db.Database.ExecuteSqlAsync(
            $"""
             INSERT INTO audit.audit_event
                 (id, occurred_at, actor_type, actor_id, application, action, entity_type, entity_id, details)
             VALUES ({id}, {occurredAt}, 'user', 'author-unchained', NULL, 'template.created', 'template', {entityId}, {"{}"}::jsonb)
             """);
}
