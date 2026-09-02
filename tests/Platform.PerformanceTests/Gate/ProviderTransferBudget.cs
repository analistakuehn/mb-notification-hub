namespace NotificationHub.PerformanceTests.Gate;

/// <summary>
/// The ceilings the transfer comparison is graded against, and the arithmetic
/// each one comes from.
/// <para>
/// They live in code and never in the reference file. The command that compares
/// a run against the reference is the same command that rewrites the reference,
/// so a ceiling kept there would be rewritten by the run it is supposed to
/// judge, and would certify itself.
/// </para>
/// </summary>
internal static class ProviderTransferBudget
{
    /// <summary>Attachments one notification may carry.</summary>
    internal const int MaxAttachmentsPerMessage = 5;

    /// <summary>Raw bytes all attachments of one notification may add up to: seven mebibytes.</summary>
    internal const long MaxTotalRawAttachmentBytes = 7_340_032;

    /// <summary>Memory of one replica: a container of two gibibytes.</summary>
    internal const long ReplicaMemoryBytes = 2L * 1_024 * 1_024 * 1_024;

    /// <summary>
    /// Memory the transfer path may hold on a replica: two hundred mebibytes,
    /// a tenth of the container rounded down to a whole number of mebibytes,
    /// which is 9,77 % of it.
    /// </summary>
    internal const long TransferPathMemoryBytes = 209_715_200;

    /// <summary>Sends a replica keeps in flight at once.</summary>
    internal const int SendsInFlightPerReplica = 8;

    /// <summary>
    /// Memory one send may cost: the share of the replica the transfer path
    /// owns, divided by the sends that hold it at the same time. Two hundred
    /// mebibytes over eight is twenty-five mebibytes. Nothing here is measured;
    /// it is the deployment target written as arithmetic, and the measurement
    /// is what it grades.
    /// </summary>
    internal const long PerSendMemoryBudgetBytes = TransferPathMemoryBytes / SendsInFlightPerReplica;

    /// <summary>
    /// Fixed part of the allocation a send may cost, whatever the attachment
    /// weighs. The rule is not fitted to any measurement: the machinery of the
    /// path may not cost more than the smallest attachment the envelope admits,
    /// which is the floor of the size axis, a quarter of a mebibyte. A ceiling
    /// of one value could not say this much, because one point has no slope and
    /// cannot tell a cost that is fixed from a cost that follows the
    /// attachment.
    /// </summary>
    internal const long AllocationConstantBytes = 256 * 1_024;

    /// <summary>
    /// Allocation a send may cost per raw byte of attachment. A path that reads
    /// in blocks and never holds the message spends nothing per byte; a path
    /// that keeps one copy of the content spends one byte per byte. The
    /// twentieth admitted here is the smallest constant fraction of the
    /// attachment that still fails a path holding a copy, and it leaves room
    /// for the read block itself. The pair is what makes the ceiling affine,
    /// and the affine form is read at the floor and at the maximum of the
    /// ratified envelope, twenty-eight times apart, so an allocation that
    /// follows the attachment cannot pass both.
    /// </summary>
    internal const double AllocationBytesPerRawByte = 0.05;

    /// <summary>
    /// Samples one arm needs before a percentile of it is a statistic. Under
    /// the nearest-rank estimator the ninety-fifth percentile of fewer than
    /// twenty samples is the maximum itself, and its spread stays wider than
    /// the tolerance until five hundred.
    /// </summary>
    internal const int MinimumSamplesPerArm = 200;

    /// <summary>
    /// Samples the ninety-ninth percentile needs before it is reported at all.
    /// Below it the run reports the highest sample it observed, named as the
    /// maximum, because that is what the estimator would have returned anyway.
    /// </summary>
    internal const int PercentileNinetyNineFloor = 1_000;

    /// <summary>
    /// Drift tolerated on the allocation ratio against the buffering arm.
    /// <para>
    /// It is wide because it was measured and not chosen: five isolated runs
    /// of the same cell moved the ratio by a factor of 1,56, since the
    /// numerator is a few tens of kilobytes and a handful of extra socket
    /// buffers move it by half. The band of 1,75 sits above that spread, and
    /// the failure it guards against is three orders of magnitude away: a
    /// candidate that started holding the message would carry a ratio near one
    /// instead of near 0,0015.
    /// </para>
    /// </summary>
    internal const double AllocationRatioTolerance = 0.75;

    /// <summary>The ceiling of the allocation a send may cost at that content size.</summary>
    internal static double AllocationCeilingBytes(long rawBytesPerOperation)
        => AllocationConstantBytes + (AllocationBytesPerRawByte * rawBytesPerOperation);
}
