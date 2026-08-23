using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

/// <summary>Result of verifying one partition in one round.</summary>
internal sealed record ChainVerificationOutcome(
    string Partition,
    bool IsIntact,
    string? Failure,
    long? BrokenSeq,
    long FromSeq,
    long ThroughSeq,
    int ChainedCount,
    bool WasFullReplay,
    byte[] TailHash);

/// <summary>
/// Replays the hash chain against the database and reports what it found. A
/// round either resumes at the checkpoint of a partition, which is cheap and
/// bounded by what arrived since the last round, or replays the partition
/// whole from its anchor, which is what re-examines the history behind the
/// checkpoint. The closing cycle always asks for the whole replay: nothing is
/// exported and nothing is detached on the strength of an incremental result.
/// </summary>
internal sealed class ChainVerifier(
    AuditDbContext db,
    AuditTrailReader reader,
    AuditPartitionCatalog catalog,
    AuditMaintenanceJournal journal,
    IOptions<ChainVerificationOptions> options,
    TimeProvider timeProvider,
    ILogger<ChainVerifier> logger)
{
    /// <summary>
    /// Verifies every attached partition and records one audit event per
    /// partition covered. Partitions are independent chains, so one broken
    /// partition never stops the others from being checked.
    /// </summary>
    public async Task<IReadOnlyList<ChainVerificationOutcome>> RunAsync(CancellationToken cancellationToken)
    {
        var outcomes = new List<ChainVerificationOutcome>();
        foreach (MonthlyPartitionWindow window in await catalog.AttachedAsync(cancellationToken))
        {
            ChainVerificationOutcome outcome = await VerifyAsync(window, forceFullReplay: false, cancellationToken);
            outcomes.Add(outcome);
        }

        return outcomes;
    }

    /// <summary>
    /// Verifies one partition, persists the checkpoint, and records the round
    /// in the trail. A full replay starts at the deterministic anchor; an
    /// incremental round starts at the checkpoint hash and never re-reads what
    /// it already covered.
    /// </summary>
    public async Task<ChainVerificationOutcome> VerifyAsync(
        MonthlyPartitionWindow window,
        bool forceFullReplay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);
        ChainVerificationOptions settings = options.Value;
        DateTimeOffset now = timeProvider.GetUtcNow();
        ChainVerificationCheckpoint? checkpoint = await db.ChainVerificationCheckpoints
            .SingleOrDefaultAsync(entry => entry.PartitionName == window.PartitionName, cancellationToken);

        var anchor = AuditChain.PartitionAnchor(window.PartitionName);
        var fullReplay = forceFullReplay
            || checkpoint is null
            || checkpoint.FullyVerifiedAt is null
            || now - checkpoint.FullyVerifiedAt.Value >= settings.FullVerificationInterval;

        var afterSeq = fullReplay ? 0 : checkpoint!.LastSeq;
        var headHash = fullReplay ? anchor : checkpoint!.LastHash;
        DateTimeOffset? watermark = forceFullReplay ? null : now - settings.StabilizationWatermark;

        ChainVerificationOutcome outcome = await FoldAsync(
            window, afterSeq, headHash, watermark, fullReplay, settings.BatchSize, cancellationToken);

        checkpoint ??= Track(ChainVerificationCheckpoint.StartAt(window.PartitionName, anchor, now));
        if (outcome.IsIntact)
        {
            checkpoint.Advance(outcome.ThroughSeq, outcome.TailHash, now, fullReplay);
            logger.ChainVerified(
                window.PartitionName, outcome.ChainedCount, outcome.FromSeq, outcome.ThroughSeq, fullReplay);
        }
        else
        {
            checkpoint.Fail(outcome.Failure!, outcome.BrokenSeq, now);
            logger.ChainVerificationFailed(window.PartitionName, outcome.BrokenSeq ?? 0, outcome.Failure!);
        }

        await db.SaveChangesAsync(cancellationToken);
        await RecordAsync(outcome, fullReplay, cancellationToken);
        return outcome;
    }

    private ChainVerificationCheckpoint Track(ChainVerificationCheckpoint checkpoint)
    {
        db.ChainVerificationCheckpoints.Add(checkpoint);
        return checkpoint;
    }

    /// <summary>
    /// Folds the chain forward in batches. The watermark stops the fold at the
    /// first row too recent to trust, never filters it out of the middle: a
    /// segment with a hole in it would fail verification for no reason.
    /// </summary>
    private async Task<ChainVerificationOutcome> FoldAsync(
        MonthlyPartitionWindow window,
        long afterSeq,
        byte[] headHash,
        DateTimeOffset? watermark,
        bool fullReplay,
        int batchSize,
        CancellationToken cancellationToken)
    {
        var running = headHash;
        var cursor = afterSeq;
        var total = 0;
        long seqMin = 0;
        long seqMax = 0;

        while (true)
        {
            IReadOnlyList<AuditTrailRow> batch = await reader.ReadRowsAsync(
                window, cursor, long.MaxValue, batchSize, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            AuditTrailRow[] usable = watermark is null
                ? [.. batch]
                : [.. batch.TakeWhile(row => row.OccurredAt < watermark.Value)];
            if (usable.Length == 0)
            {
                break;
            }

            AuditTrailRow[] chainedRows = [.. usable.Where(row => !row.IsUnchained)];
            AuditTrailRow? drifted = Array.Find(chainedRows, row => row.CanonicalDrift() is not null);
            if (drifted is not null)
            {
                return new ChainVerificationOutcome(
                    window.PartitionName,
                    IsIntact: false,
                    $"canonical-drift:{drifted.CanonicalDrift()}",
                    drifted.Seq,
                    afterSeq,
                    cursor,
                    total,
                    fullReplay,
                    running);
            }

            AuditChainRow[] chained =
            [
                .. chainedRows.Select(row => new AuditChainRow(row.Seq, row.Canonical!, row.PrevHash, row.Hash)),
            ];

            AuditChainSegmentResult segment = AuditChainSegment.Verify(running, chained);
            if (!segment.IsIntact)
            {
                return new ChainVerificationOutcome(
                    window.PartitionName,
                    IsIntact: false,
                    segment.Reason,
                    segment.BrokenSeq,
                    afterSeq,
                    cursor,
                    total,
                    fullReplay,
                    running);
            }

            if (chained.Length > 0)
            {
                seqMin = seqMin == 0 ? segment.SeqMin : seqMin;
                seqMax = segment.SeqMax;
                total += chained.Length;
            }

            running = segment.TailHash;
            cursor = usable[^1].Seq;
            if (usable.Length < batch.Count || batch.Count < batchSize)
            {
                break;
            }
        }

        return new ChainVerificationOutcome(
            window.PartitionName,
            IsIntact: true,
            Failure: null,
            BrokenSeq: null,
            seqMin,
            cursor,
            total,
            fullReplay,
            running);
    }

    private async Task RecordAsync(
        ChainVerificationOutcome outcome,
        bool fullReplay,
        CancellationToken cancellationToken)
        => await journal.RecordAsync(
            outcome.IsIntact ? AuditActions.AuditChainVerified : AuditActions.AuditChainVerificationFailed,
            outcome.Partition,
            [
                ("chainedCount", outcome.ChainedCount),
                ("failure", outcome.Failure),
                ("brokenSeq", outcome.BrokenSeq),
                ("fromSeq", outcome.FromSeq),
                ("throughSeq", outcome.ThroughSeq),
                ("fullReplay", fullReplay),
                ("tailHash", AuditHex.ToHex(outcome.TailHash)),
            ],
            cancellationToken);
}
