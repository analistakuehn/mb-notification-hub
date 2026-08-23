namespace NotificationHub.Api.Modules.Audit.Domain;

/// <summary>
/// One row as the chain sees it. The stored hashes are optional because the
/// same arithmetic runs over two sources: the database, where every chained
/// row carries its own <c>prev_hash</c> and <c>hash</c>, and the exported
/// evidence, where only the canonical text travels and the hashes are
/// rebuilt.
/// </summary>
internal sealed record AuditChainRow(long Seq, string Canonical, byte[]? PrevHash, byte[]? Hash);

/// <summary>
/// Outcome of folding a chain segment: where it ended, how long it was, and
/// the sequence number of the first link that did not hold.
/// </summary>
internal sealed record AuditChainSegmentResult(
    bool IsIntact,
    long? BrokenSeq,
    string? Reason,
    byte[] TailHash,
    int Count,
    long SeqMin,
    long SeqMax);

/// <summary>
/// Pure verification of a contiguous chain segment: fold
/// <c>hash = SHA-256(prev_hash ‖ canonical)</c> from a known starting hash and
/// compare every link. Holes in <c>seq</c> are legitimate (aborted
/// transactions consume values), so the arithmetic never assumes consecutive
/// numbers; what it does require is that the rows arrive in chain order, which
/// inside a monthly partition is sequence order.
/// </summary>
internal static class AuditChainSegment
{
    /// <summary>
    /// Folds <paramref name="rows"/> from <paramref name="headPrevHash"/>. A
    /// stored hash that disagrees with the recomputed one, a stored
    /// predecessor that disagrees with the running hash, or a sequence that
    /// goes backwards stops the fold and names the offending row.
    /// </summary>
    internal static AuditChainSegmentResult Verify(byte[] headPrevHash, IReadOnlyList<AuditChainRow> rows)
    {
        ArgumentNullException.ThrowIfNull(headPrevHash);
        ArgumentNullException.ThrowIfNull(rows);

        var running = headPrevHash;
        long? previousSeq = null;
        foreach (AuditChainRow row in rows)
        {
            if (previousSeq is not null && row.Seq <= previousSeq)
            {
                return Broken(row.Seq, "sequence-out-of-order", running, rows);
            }

            if (row.PrevHash is not null && !row.PrevHash.AsSpan().SequenceEqual(running))
            {
                return Broken(row.Seq, "prev-hash-mismatch", running, rows);
            }

            var computed = AuditChain.Link(running, row.Canonical);
            if (row.Hash is not null && !row.Hash.AsSpan().SequenceEqual(computed))
            {
                return Broken(row.Seq, "hash-mismatch", running, rows);
            }

            running = computed;
            previousSeq = row.Seq;
        }

        return new AuditChainSegmentResult(
            IsIntact: true,
            BrokenSeq: null,
            Reason: null,
            TailHash: running,
            Count: rows.Count,
            SeqMin: rows.Count == 0 ? 0 : rows[0].Seq,
            SeqMax: rows.Count == 0 ? 0 : rows[^1].Seq);
    }

    private static AuditChainSegmentResult Broken(
        long seq,
        string reason,
        byte[] running,
        IReadOnlyList<AuditChainRow> rows)
        => new(
            IsIntact: false,
            BrokenSeq: seq,
            Reason: reason,
            TailHash: running,
            Count: rows.Count,
            SeqMin: rows.Count == 0 ? 0 : rows[0].Seq,
            SeqMax: rows.Count == 0 ? 0 : rows[^1].Seq);
}
