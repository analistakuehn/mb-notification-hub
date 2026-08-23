using System.Security.Cryptography;
using System.Text;
using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.UnitTests.Audit;

/// <summary>
/// The arithmetic both verifiers run: the one that reads the database and the
/// one that reads only the exported bytes. Everything here is a pure function
/// of a starting hash and a list of rows.
/// </summary>
public sealed class AuditChainSegmentTests
{
    private static readonly byte[] Head = SHA256.HashData("cabeça"u8.ToArray());

    [Fact]
    public void An_intact_segment_folds_to_the_hash_of_its_last_row()
    {
        AuditChainRow[] rows = [Row(1, "a", Head, out var first), Row(2, "b", first, out var second)];

        AuditChainSegmentResult result = AuditChainSegment.Verify(Head, rows);

        result.IsIntact.ShouldBeTrue();
        result.TailHash.ShouldBe(second);
        result.Count.ShouldBe(2);
        result.SeqMin.ShouldBe(1);
        result.SeqMax.ShouldBe(2);
    }

    [Fact]
    public void Holes_in_the_sequence_are_not_damage()
    {
        // An aborted transaction consumes a sequence value; the chain simply
        // skips the number and stays intact.
        AuditChainRow[] rows = [Row(1, "a", Head, out var first), Row(97, "b", first, out _)];

        AuditChainSegment.Verify(Head, rows).IsIntact.ShouldBeTrue();
    }

    [Fact]
    public void A_row_whose_stored_hash_does_not_cover_its_canonical_text_breaks_the_segment()
    {
        AuditChainRow intact = Row(1, "a", Head, out var first);
        AuditChainRow tampered = intact with { Canonical = "a-editado" };

        AuditChainSegmentResult result = AuditChainSegment.Verify(Head, [tampered]);

        result.IsIntact.ShouldBeFalse();
        result.Reason.ShouldBe("hash-mismatch");
        result.BrokenSeq.ShouldBe(1);
        first.ShouldNotBeNull();
    }

    [Fact]
    public void A_row_that_points_at_the_wrong_predecessor_breaks_the_segment()
    {
        AuditChainRow first = Row(1, "a", Head, out var firstHash);
        AuditChainRow orphan = Row(2, "b", SHA256.HashData("outro"u8.ToArray()), out _);

        AuditChainSegmentResult result = AuditChainSegment.Verify(Head, [first, orphan]);

        result.IsIntact.ShouldBeFalse();
        result.Reason.ShouldBe("prev-hash-mismatch");
        result.BrokenSeq.ShouldBe(2);
        firstHash.ShouldNotBeNull();
    }

    [Fact]
    public void Rows_out_of_chain_order_break_the_segment()
    {
        AuditChainRow first = Row(5, "a", Head, out var firstHash);
        AuditChainRow second = Row(4, "b", firstHash, out _);

        AuditChainSegmentResult result = AuditChainSegment.Verify(Head, [first, second]);

        result.IsIntact.ShouldBeFalse();
        result.Reason.ShouldBe("sequence-out-of-order");
    }

    [Fact]
    public void Rows_without_stored_hashes_are_folded_from_the_head_alone()
    {
        // This is the exported shape: no hash travels per line, so the fold is
        // what reproduces the tail the manifest claims.
        AuditChainRow stored = Row(1, "a", Head, out var expected);
        AuditChainRow exported = new(stored.Seq, stored.Canonical, null, null);

        AuditChainSegment.Verify(Head, [exported]).TailHash.ShouldBe(expected);
    }

    [Fact]
    public void An_empty_segment_carries_the_head_forward_unchanged()
    {
        AuditChainSegmentResult result = AuditChainSegment.Verify(Head, []);

        result.IsIntact.ShouldBeTrue();
        result.TailHash.ShouldBe(Head);
        result.SeqMin.ShouldBe(0);
        result.SeqMax.ShouldBe(0);
    }

    private static AuditChainRow Row(long seq, string canonical, byte[] previous, out byte[] hash)
    {
        hash = SHA256.HashData([.. previous, .. Encoding.UTF8.GetBytes(canonical)]);
        return new AuditChainRow(seq, canonical, previous, hash);
    }
}
