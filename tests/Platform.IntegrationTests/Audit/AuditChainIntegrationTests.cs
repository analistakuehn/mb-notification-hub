using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.IntegrationTests.TemplateManagement;

namespace NotificationHub.IntegrationTests.Audit;

[Collection(TemplateManagementApiCollectionDefinition.Name)]
public sealed class AuditChainIntegrationTests(TemplateManagementApiFixture fixture)
{
    [RequiresDockerFact]
    public async Task Two_appends_in_the_same_partition_link_prev_hash_and_hash_correctly()
    {
        var firstEntity = $"chain-two-1-{Guid.CreateVersion7():N}";
        var secondEntity = $"chain-two-2-{Guid.CreateVersion7():N}";

        await AppendAsync(firstEntity);
        await AppendAsync(secondEntity);

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            AuditEvent first = await db.AuditEvents.AsNoTracking()
                .SingleAsync(auditEvent => auditEvent.EntityId == firstEntity);
            AuditEvent second = await db.AuditEvents.AsNoTracking()
                .SingleAsync(auditEvent => auditEvent.EntityId == secondEntity);

            // Every link is recomputable from the stored columns alone: the
            // hash covers the predecessor hash followed by the exact canonical
            // bytes, and the second event chains onto the first.
            first.Hash.ShouldBe(RecomputeLink(first.PrevHash!, first.Canonical!));
            second.PrevHash.ShouldBe(first.Hash);
            second.Hash.ShouldBe(RecomputeLink(second.PrevHash!, second.Canonical!));
        });
    }

    [RequiresDockerFact]
    public async Task Concurrent_appends_serialize_under_the_advisory_lock_without_forking_the_chain()
    {
        var marker = $"chain-conc-{Guid.CreateVersion7():N}";
        await Task.WhenAll(Enumerable.Range(0, 16)
            .Select(index => AppendAsync($"{marker}-{index}")));

        await fixture.ExecuteAuditDbAsync(async db =>
        {
            List<AuditEvent> chained = await db.AuditEvents.AsNoTracking()
                .Where(auditEvent => auditEvent.Hash != null)
                .OrderBy(auditEvent => auditEvent.Seq)
                .ToListAsync();
            chained.Count(auditEvent => auditEvent.EntityId.StartsWith(marker, StringComparison.Ordinal))
                .ShouldBe(16);

            // The whole store must hold one linear chain per monthly
            // partition: the first chained event links to the partition
            // anchor, every later one to its predecessor's hash, and every
            // hash is recomputable from the stored bytes. A fork or a gap
            // anywhere breaks one of these links.
            //
            // Walking in sequence order is also what proves the sequence value
            // is taken with the lock already held: a value reserved before the
            // lock would order the rows differently from the way they chained,
            // and the walk would land on the wrong predecessor.
            foreach (IGrouping<(int Year, int Month), AuditEvent> partition in chained.GroupBy(MonthOf))
            {
                var expectedPrev = PartitionAnchor(partition.Key);
                foreach (AuditEvent auditEvent in partition)
                {
                    auditEvent.PrevHash.ShouldBe(expectedPrev);
                    auditEvent.Hash.ShouldBe(RecomputeLink(auditEvent.PrevHash!, auditEvent.Canonical!));
                    expectedPrev = auditEvent.Hash!;
                }

                // Stated directly, because a fork is two rows claiming the same
                // predecessor: a chain that branched and then rejoined would
                // still fail the walk above, but this is the property the trail
                // exists to guarantee and it deserves its own assertion.
                partition.Select(auditEvent => Convert.ToHexString(auditEvent.PrevHash!))
                    .Distinct(StringComparer.Ordinal)
                    .Count()
                    .ShouldBe(partition.Count());
            }
        });
    }

    private async Task AppendAsync(string entityId)
    {
        var trail = new TransactionalAuditTrail();
        await using var connection = new NpgsqlConnection(fixture.PostgresConnectionString);
        await connection.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync();
        await trail.AppendAsync(transaction, new AuditEntry
        {
            ActorType = AuditActorTypes.System,
            ActorId = "chain-tests",
            Action = AuditActions.TemplateCreated,
            EntityType = AuditEntityTypes.Template,
            EntityId = entityId,
            DetailsJson = """{"origin":"chain-integration-test"}""",
            OccurredAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
        await transaction.CommitAsync();
    }

    private static (int Year, int Month) MonthOf(AuditEvent auditEvent)
        => (auditEvent.OccurredAt.UtcDateTime.Year, auditEvent.OccurredAt.UtcDateTime.Month);

    // Recomputed from the documented preimage, not from the production helper,
    // so a drift in the anchor rule fails here instead of passing silently.
    private static byte[] PartitionAnchor((int Year, int Month) month)
        => SHA256.HashData(Encoding.UTF8.GetBytes(
            $"notification-hub:audit-chain:audit_event_{month.Year:D4}_{month.Month:D2}:anchor"));

    private static byte[] RecomputeLink(byte[] prevHash, string canonical)
        => SHA256.HashData([.. prevHash, .. Encoding.UTF8.GetBytes(canonical)]);
}
