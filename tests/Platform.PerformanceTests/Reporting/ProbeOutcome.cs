using NotificationHub.PerformanceTests.Contention;
using NotificationHub.PerformanceTests.Instrumentation;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Reporting;

/// <summary>
/// The planning numbers the verdict is read against. They are arithmetic over
/// the capacity model of the design, not measurements: three trail rows per
/// notification, the sustained and peak notification rates, and the share of
/// the acceptance budget the chain is allowed to spend.
/// </summary>
internal static class Demand
{
    internal const int AppendsPerNotification = 3;

    internal const int SustainedNotificationsPerSecond = 300;

    internal const int PeakNotificationsPerSecond = 1_000;

    /// <summary>Half the ceiling, because a queue explodes long before the mean saturates.</summary>
    internal const double TargetUtilization = 0.5;

    /// <summary>Share of the fifty milliseconds the design gives the whole REST acceptance.</summary>
    internal const double WindowBudgetMs = 10;

    internal static int SustainedAppendsPerSecond
        => SustainedNotificationsPerSecond * AppendsPerNotification;

    internal static int PeakAppendsPerSecond
        => PeakNotificationsPerSecond * AppendsPerNotification;

    internal static double RequiredCeiling => SustainedAppendsPerSecond / TargetUtilization;

    /// <summary>Hold window that the sustained demand implies, in milliseconds.</summary>
    internal static double RequiredHoldMs => 1_000 / RequiredCeiling;

    /// <summary>Hold window that the peak demand implies, in milliseconds.</summary>
    internal static double RequiredPeakHoldMs => 1_000 / (PeakAppendsPerSecond / TargetUtilization);
}

/// <summary>Capacity rule: the implied ceiling of one partition against the demand it must carry.</summary>
internal sealed record CapacityCheck(string ArmId, int Volume, double HoldP50Ms, double Ceiling, bool Passes);

/// <summary>Sub-budget rule: waiting plus holding inside the acceptance budget.</summary>
internal sealed record BudgetCheck(string ArmId, int Volume, double WindowP99Ms, int Samples, bool Passes);

/// <summary>How the ceiling of a partition moves with the commit latency of the target database.</summary>
internal sealed record SensitivityRow(
    string ArmId,
    int Volume,
    double PreCommitP50Ms,
    IReadOnlyDictionary<string, double> CeilingByCommitLatency);

/// <summary>Delta between the control and the treatment at one volume.</summary>
internal sealed record ContentionRatio(
    int Volume,
    double ControlLatencyP50Ms,
    double TreatmentLatencyP50Ms,
    double LatencyRatio,
    double ControlHoldP50Ms,
    double TreatmentHoldP50Ms,
    double ControlThroughput,
    double TreatmentThroughput);

/// <summary>The escalation ladder's answer, with the numbers that produced it.</summary>
internal sealed record PlanBVerdict(
    bool MitigationsSuffice,
    bool Triggered,
    string Summary,
    IReadOnlyList<CapacityCheck> Capacity,
    IReadOnlyList<BudgetCheck> Budget);

/// <summary>Where the run happened, so a number is never read out of its context.</summary>
internal sealed record ProbeEnvironment(
    string Host,
    int ProcessorCount,
    string Runtime,
    string Target,
    bool Throwaway,
    int Appenders,
    double ArmSeconds);

/// <summary>Everything one probe run produced.</summary>
internal sealed record ProbeOutcome(
    string GeneratedAtUtc,
    string Mode,
    ProbeEnvironment Environment,
    IReadOnlyList<ArmResult> Arms,
    PhaseStatistics? RoundTrip,
    string TailIndexSource,
    IReadOnlyList<ContentionRatio> Ratios,
    IReadOnlyList<SensitivityRow> Sensitivity,
    IReadOnlyList<SustainedRateResult> Sustained,
    TailIndexChoice? TailIndex,
    IReadOnlyList<ReadPathPlan> ReadPaths,
    InterferenceResult? Interference,
    IReadOnlyList<RelayPlan> RelayPlans,
    IReadOnlyList<VerificationCost> Verification,
    IReadOnlyList<FallbackLatency> FallbackLatencies,
    IReadOnlyList<WebhookIngestionCost> WebhookIngestion,
    PlanBVerdict? Verdict);

/// <summary>Process and logical-I/O measurements for one transfer method.</summary>
internal sealed record AttachmentTransferArm(
    string ArmId,
    int PayloadUtf8Bytes,
    int EnvelopeBytes,
    int BytesPerOperation,
    int Operations,
    int ConfiguredConcurrency,
    int PeakConcurrency,
    int InitialBacklog,
    string ExpectedDigest,
    IReadOnlyList<string> ObservedDigests,
    bool DigestsEqual,
    long HeapBytesBefore,
    long HeapBytesAfter,
    long WorkingSetBytesBefore,
    long WorkingSetBytesAfter,
    long AllocatedBytes,
    int Generation0Collections,
    int Generation1Collections,
    int Generation2Collections,
    double CpuMilliseconds,
    double ElapsedMilliseconds,
    double LatencyP50Milliseconds,
    double LatencyP95Milliseconds,
    double LatencyP99Milliseconds,
    double ThroughputBytesPerSecond,
    long? LogicalFileReadBytes,
    long? LogicalFileWrittenBytes,
    int TemporaryFilesCreated,
    int TemporaryFilesRemaining,
    bool TemporaryRootRemoved);

/// <summary>Everything produced by one attachment-transfer comparison.</summary>
internal sealed record AttachmentTransferOutcome(
    string RecordedAtUtc,
    string Host,
    int Processors,
    string Runtime,
    int PayloadUtf8Bytes,
    int EnvelopeBytes,
    int OperationsPerArm,
    int ConfiguredConcurrency,
    string ExpectedDigest,
    IReadOnlyList<AttachmentTransferArm> Arms);

/// <summary>What the provider double read back for one attachment.</summary>
internal sealed record ProviderTransferAttachmentCheck(
    int Order,
    string FileName,
    string ContentType,
    long SourceBytes,
    long Base64Bytes,
    long DecodedBytes,
    bool DigestMatchesSource,
    bool MetadataMatches);

/// <summary>Functional evidence and process cost of one transfer method.</summary>
internal sealed record ProviderTransferArm(
    string ArmId,
    int Operations,
    int ConfiguredConcurrency,
    int PeakConcurrency,
    int ProviderCalls,
    int AcceptedCalls,
    long CapturedBodyBytes,
    long DeclaredContentLengthBytes,
    string CapturedBodySha256,
    int DistinctCapturedDigests,
    bool ContentLengthDeclared,
    bool ChunkedObserved,
    IReadOnlyList<ProviderTransferAttachmentCheck> Attachments,
    long PeakHeapBytes,
    long PeakWorkingSetBytes,
    int ResidencySamples,
    long AllocatedBytes,
    int Generation0Collections,
    int Generation1Collections,
    int Generation2Collections,
    double CollectionPauseMilliseconds,
    double CpuMilliseconds,
    double ElapsedMilliseconds,
    int LatencySamples,
    double LatencyP50Milliseconds,
    double LatencyP95Milliseconds,
    double? LatencyP99Milliseconds,
    double LatencyMaxMilliseconds,
    double ThroughputBytesPerSecond,
    int TemporaryFilesCreated,
    int TemporaryFilesRemaining,
    bool TemporaryRootRemoved,
    int OpenSourceStreams)
{
    internal double AllocatedBytesPerOperation => AllocatedBytes / (double)Operations;

    internal double Generation2CollectionsPerOperation => Generation2Collections / (double)Operations;

    internal double CollectionPauseMillisecondsPerOperation => CollectionPauseMilliseconds / Operations;
}

/// <summary>Everything produced by one provider-transfer comparison.</summary>
internal sealed record ProviderTransferOutcome(
    string RecordedAtUtc,
    string Host,
    int Processors,
    string Runtime,
    bool ServerGarbageCollection,
    int GarbageCollectorHeapCount,
    string GarbageCollectorLatencyMode,
    string ProfileId,
    string ContentShape,
    long AttachmentBytes,
    int AttachmentCount,
    long TotalRawAttachmentBytes,
    long Base64Bytes,
    long EnvelopeBytes,
    long BodyBytes,
    long MessageCeilingBytes,
    int SourceChunkBytes,
    double SourceLatencyMilliseconds,
    int OperationsPerArm,
    int ConfiguredConcurrency,
    bool ContentLengthDeclared,
    string SourceContentSha256,
    bool ArmsAgreeOnBody,
    IReadOnlyList<ProviderTransferArm> Arms);

/// <summary>Turns the raw arm results into the checks the slice has to answer.</summary>
internal static class ProbeAnalysis
{
    private static readonly double[] CommitLatencies = [0.5, 1, 2, 4];

    internal static IReadOnlyList<ContentionRatio> Ratios(IReadOnlyList<ArmResult> arms)
    {
        ArgumentNullException.ThrowIfNull(arms);
        var ratios = new List<ContentionRatio>();
        foreach (var volume in arms.Select(arm => arm.Volume).Distinct())
        {
            ArmResult? control = arms.FirstOrDefault(arm => arm.ArmId == "A1" && arm.Volume == volume);
            ArmResult? treatment = arms.FirstOrDefault(arm => arm.ArmId == "A2" && arm.Volume == volume);
            if (control is null || treatment is null)
            {
                continue;
            }

            ratios.Add(new ContentionRatio(
                volume,
                control.Latency.P50,
                treatment.Latency.P50,
                control.Latency.P50 > 0 ? treatment.Latency.P50 / control.Latency.P50 : double.NaN,
                control.Hold.P50,
                treatment.Hold.P50,
                control.AppendsPerSecond,
                treatment.AppendsPerSecond));
        }

        return ratios;
    }

    internal static IReadOnlyList<SensitivityRow> Sensitivity(IReadOnlyList<ArmResult> arms)
    {
        ArgumentNullException.ThrowIfNull(arms);
        return
        [
            .. arms
                .Where(arm => arm.ArmId is "A3" or "A5")
                .Select(arm => new SensitivityRow(
                    arm.ArmId,
                    arm.Volume,
                    arm.PreCommit.P50,
                    CommitLatencies.ToDictionary(
                        latency => latency.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture),
                        latency => 1_000 / (arm.PreCommit.P50 + latency)))),
        ];
    }

    /// <summary>
    /// Volumes where the control arm holds the lock longer than the treatment
    /// arm. It reads like an anomaly and is not one: without an index, four
    /// appenders on four partitions run four concurrent scans of the same
    /// partition size, while four appenders on one partition serialize and keep
    /// a single scan hot in cache. The serialization was protecting the
    /// database. It also bounds the method: the contention delta is only clean
    /// where the scan is cheap.
    /// </summary>
    internal static IReadOnlyList<int> VolumesWhereControlCostsMore(IReadOnlyList<ArmResult> arms)
    {
        ArgumentNullException.ThrowIfNull(arms);
        var inverted = new List<int>();
        foreach (var volume in arms.Select(arm => arm.Volume).Distinct())
        {
            ArmResult? control = arms.FirstOrDefault(arm => arm.ArmId == "A1" && arm.Volume == volume);
            ArmResult? treatment = arms.FirstOrDefault(arm => arm.ArmId == "A2" && arm.Volume == volume);
            if (control is not null && treatment is not null && control.Hold.P50 > treatment.Hold.P50)
            {
                inverted.Add(volume);
            }
        }

        return inverted;
    }

    internal static PlanBVerdict Verdict(IReadOnlyList<ArmResult> arms)
    {
        ArgumentNullException.ThrowIfNull(arms);
        var capacity = new List<CapacityCheck>();
        var budget = new List<BudgetCheck>();
        foreach (ArmResult arm in arms.Where(arm => arm.ArmId is "A3" or "A4" or "A5"))
        {
            var ceiling = arm.Hold.P50 > 0 ? 1_000 / arm.Hold.P50 : double.NaN;
            capacity.Add(new CapacityCheck(
                arm.ArmId, arm.Volume, arm.Hold.P50, ceiling, ceiling >= Demand.RequiredCeiling));
            budget.Add(new BudgetCheck(
                arm.ArmId,
                arm.Volume,
                arm.Window.P99,
                arm.Window.Samples,
                arm.Window.P99 <= Demand.WindowBudgetMs));
        }

        var mitigated = capacity.Where(check => check.ArmId == "A5").ToList();
        var suffice = mitigated.Count > 0 && mitigated.TrueForAll(check => check.Passes);
        var summary = suffice
            ? "As mitigações do braço A5 mantêm o teto por partição acima do exigido em todos os volumes medidos."
            : "O braço A5 não sustenta o teto exigido em ao menos um volume medido.";
        return new PlanBVerdict(suffice, !suffice, summary, capacity, budget);
    }
}
