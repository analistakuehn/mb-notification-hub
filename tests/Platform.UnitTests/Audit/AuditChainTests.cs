using System.Security.Cryptography;
using System.Text;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.UnitTests.Audit;

public sealed class AuditChainTests
{
    private static readonly Guid EventId = Guid.Parse("01890000-0000-7000-8000-000000000001");

    // 123456 microseconds plus a stray 100-nanosecond tick the store cannot keep.
    private static readonly DateTimeOffset OccurredAt =
        new DateTimeOffset(2026, 8, 22, 15, 0, 0, TimeSpan.Zero).AddTicks(1234567);

    private const string ExpectedCanonical =
        """{"action":"template.version.published","actorId":"publisher-1","actorType":"user","application":"araia-cambio","details":{"a":{"x":[1,2],"y":"í"},"b":1},"entityId":"auth.otp.login:3","entityType":"template_version","id":"01890000-0000-7000-8000-000000000001","occurredAt":"2026-08-22T15:00:00.123456Z","seq":42}""";

    [Fact]
    public void The_canonical_document_is_compact_with_ordinal_key_order_and_canonicalized_details()
        => AuditChain.CanonicalDocument(EventId, 42, Entry()).ShouldBe(ExpectedCanonical);

    [Fact]
    public void The_canonical_document_is_deterministic_across_calls()
        => AuditChain.CanonicalDocument(EventId, 42, Entry())
            .ShouldBe(AuditChain.CanonicalDocument(EventId, 42, Entry()));

    [Fact]
    public void The_canonical_timestamp_is_truncated_to_microseconds_in_utc()
    {
        DateTimeOffset zoned = new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.FromHours(-3)).AddTicks(9);

        var canonical = AuditChain.CanonicalDocument(EventId, 1, Entry() with { OccurredAt = zoned });

        canonical.ShouldContain("\"occurredAt\":\"2026-08-22T15:00:00.000000Z\"");
    }

    [Fact]
    public void An_absent_application_is_encoded_as_json_null()
        => AuditChain.CanonicalDocument(EventId, 1, Entry() with { Application = null })
            .ShouldContain("\"application\":null");

    [Fact]
    public void The_link_hash_covers_the_predecessor_hash_followed_by_the_canonical_bytes()
    {
        var prevHash = SHA256.HashData(Encoding.UTF8.GetBytes("seed"));

        var hash = AuditChain.Link(prevHash, "abc");

        Convert.ToHexStringLower(hash)
            .ShouldBe("ccfcae2f71f37f141ef5d4b52f866f12e95d07c754a083662cccd93baeb74c7d");
    }

    [Fact]
    public void The_first_link_of_a_partition_chains_the_full_document_onto_the_anchor()
    {
        var anchor = AuditChain.PartitionAnchor("audit_event_2026_08");

        var hash = AuditChain.Link(anchor, AuditChain.CanonicalDocument(EventId, 42, Entry()));

        Convert.ToHexStringLower(hash)
            .ShouldBe("92dab8f7fd5c31e36b1621c65b010ca1406da376908600a9324606fb2bb83e4d");
    }

    [Fact]
    public void The_partition_anchor_hashes_the_documented_preimage()
        => Convert.ToHexStringLower(AuditChain.PartitionAnchor("audit_event_2026_08"))
            .ShouldBe("29db3f90183d8f4fb773b173fa62d1530dfc2c4eb1953c8f7485652431b763a6");

    [Fact]
    public void The_partition_lock_key_scopes_the_advisory_keyspace_and_encodes_the_month()
        => AuditChain.PartitionLockKey(2026, 8).ShouldBe(4707744065809225584L);

    [Fact]
    public void Different_months_take_different_lock_keys()
        => AuditChain.PartitionLockKey(2026, 8).ShouldNotBe(AuditChain.PartitionLockKey(2026, 9));

    private static AuditEntry Entry() => new()
    {
        ActorType = AuditActorTypes.User,
        ActorId = "publisher-1",
        Application = "araia-cambio",
        Action = AuditActions.TemplateVersionPublished,
        EntityType = AuditEntityTypes.TemplateVersion,
        EntityId = "auth.otp.login:3",
        DetailsJson = """{"b":1,"a":{"y":"í","x":[1,2]}}""",
        OccurredAt = OccurredAt,
    };
}
