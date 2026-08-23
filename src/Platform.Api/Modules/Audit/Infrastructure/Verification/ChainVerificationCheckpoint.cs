namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>
/// How far the periodic verification has got on one partition. This is job
/// state, not trail content: it is mutable by design and lives in its own
/// table, because writing progress into an append-only trail would either
/// corrupt the append-only guarantee or bury the trail under bookkeeping.
/// The durable record of each round stays where it belongs, in the trail
/// itself, as one audit event per round.
/// </summary>
internal sealed class ChainVerificationCheckpoint
{
    private ChainVerificationCheckpoint(string partitionName, byte[] lastHash, DateTimeOffset verifiedAt)
    {
        PartitionName = partitionName;
        LastHash = lastHash;
        VerifiedAt = verifiedAt;
    }

    // EF Core materialization.
    private ChainVerificationCheckpoint()
    {
        PartitionName = null!;
        LastHash = null!;
    }

    public string PartitionName { get; private set; }

    /// <summary>Highest sequence already covered; the next round resumes after it.</summary>
    public long LastSeq { get; private set; }

    /// <summary>Chain state at <see cref="LastSeq"/>; the next round folds onto it.</summary>
    public byte[] LastHash { get; private set; }

    public DateTimeOffset VerifiedAt { get; private set; }

    /// <summary>When the whole partition was last replayed from its anchor.</summary>
    public DateTimeOffset? FullyVerifiedAt { get; private set; }

    /// <summary>Reason of the last failure; null while the partition verifies clean.</summary>
    public string? Failure { get; private set; }

    public long? FailedSeq { get; private set; }

    /// <summary>Starts tracking a partition at its deterministic anchor.</summary>
    public static ChainVerificationCheckpoint StartAt(
        string partitionName,
        byte[] anchor,
        DateTimeOffset verifiedAt)
        => new(partitionName, anchor, verifiedAt);

    /// <summary>Records a clean round and moves the resume point forward.</summary>
    public void Advance(long lastSeq, byte[] lastHash, DateTimeOffset verifiedAt, bool wasFullReplay)
    {
        LastSeq = lastSeq;
        LastHash = lastHash;
        VerifiedAt = verifiedAt;
        Failure = null;
        FailedSeq = null;
        if (wasFullReplay)
        {
            FullyVerifiedAt = verifiedAt;
        }
    }

    /// <summary>
    /// Records a broken link. The resume point does not move: a partition that
    /// failed keeps failing on every round until someone looks at it, which is
    /// the intended behavior for an integrity alarm.
    /// </summary>
    public void Fail(string failure, long? failedSeq, DateTimeOffset verifiedAt)
    {
        Failure = failure;
        FailedSeq = failedSeq;
        VerifiedAt = verifiedAt;
    }
}
