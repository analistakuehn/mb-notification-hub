using System.Text;
using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.UnitTests.Audit;

/// <summary>
/// The manifest is the artifact an auditor reads years from now, so its shape
/// is a contract: the same facts must always produce the same bytes, and every
/// field must survive a round trip.
/// </summary>
public sealed class AuditExportManifestTests
{
    [Fact]
    public void The_canonical_manifest_is_compact_with_ordinal_key_order()
    {
        var text = Encoding.UTF8.GetString(Sample().CanonicalBytes());

        text.ShouldBe(
            """{"anchor":"aa","chainedCount":2,"compressedDigest":"cc","formatVersion":1,"headPrevHash":"hh","partition":"audit_event_2026_08","previous":{"key":"audit-export/v1/audit_event/audit_event_2026_08/daily/2026-08-01/manifest.json","partition":"audit_event_2026_08","tailHash":"hh"},"seqMax":9,"seqMin":4,"table":"audit_event","tailHash":"tt","type":"daily","uncompressedDigest":"uu","unchainedCount":0,"unchainedDigest":null,"windowFrom":"2026-08-02T00:00:00.000000Z","windowTo":"2026-08-03T00:00:00.000000Z"}""");
    }

    [Fact]
    public void The_same_facts_always_produce_the_same_bytes()
    {
        // The rerun of an export recognizes its own object by digest; a
        // manifest that carried a clock or a run id would never match.
        Sample().CanonicalBytes().ShouldBe(Sample().CanonicalBytes());
    }

    [Fact]
    public void Every_field_survives_the_round_trip()
    {
        var parsed = AuditExportManifest.Parse(Sample().CanonicalBytes());

        parsed.ShouldBe(Sample());
    }

    [Fact]
    public void A_manifest_without_a_predecessor_states_it_instead_of_omitting_it()
    {
        AuditExportManifest first = Sample() with { Previous = null, UnchainedDigest = "nn", UnchainedCount = 3 };

        var text = Encoding.UTF8.GetString(first.CanonicalBytes());

        text.ShouldContain("\"previous\":null");
        text.ShouldContain("\"unchainedDigest\":\"nn\"");
        AuditExportManifest.Parse(first.CanonicalBytes()).Previous.ShouldBeNull();
    }

    private static AuditExportManifest Sample()
        => new()
        {
            FormatVersion = AuditExportManifest.CurrentFormatVersion,
            Table = "audit_event",
            Partition = "audit_event_2026_08",
            Type = AuditExportManifest.DailyType,
            WindowFrom = new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
            WindowTo = new DateTimeOffset(2026, 8, 3, 0, 0, 0, TimeSpan.Zero),
            SeqMin = 4,
            SeqMax = 9,
            ChainedCount = 2,
            UnchainedCount = 0,
            Anchor = "aa",
            HeadPrevHash = "hh",
            TailHash = "tt",
            UncompressedDigest = "uu",
            CompressedDigest = "cc",
            UnchainedDigest = null,
            Previous = new AuditExportManifestLink(
                "audit-export/v1/audit_event/audit_event_2026_08/daily/2026-08-01/manifest.json",
                "audit_event_2026_08",
                "hh"),
        };
}
