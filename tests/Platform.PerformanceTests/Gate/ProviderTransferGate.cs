using System.Globalization;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>
/// The closed set of the provider-transfer comparison: what one run answers on
/// its own, and what it answers only against the versioned reference.
/// <para>
/// The reference carries ratios and configuration and no ceiling at all. Every
/// ceiling is a constant of this assembly, because the command that compares a
/// run against the reference is the command that rewrites it.
/// </para>
/// </summary>
internal static class ProviderTransferGate
{
    internal static GateOutcome Evaluate(
        ProviderTransferBaseline baseline,
        ProviderTransferOutcome outcome,
        string candidateArm)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateArm);

        var checks = new List<GateCheck>(ProviderTransferInvariants.Checks(outcome, candidateArm));
        ProviderTransferCellBaseline? cell = baseline.CellFor(outcome.ProfileId, outcome.ConfiguredConcurrency);
        if (cell is null)
        {
            checks.Add(new GateCheck(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"referência: célula do perfil {outcome.ProfileId} na concorrência "
                    + $"{outcome.ConfiguredConcurrency}"),
                1,
                0,
                1,
                Passes: false));
            return new GateOutcome(false, ProviderTransferBudget.AllocationRatioTolerance, checks);
        }

        AddConfiguration(checks, cell, outcome);
        AddCollector(checks, baseline, outcome);
        AddRatios(checks, cell, outcome);
        return new GateOutcome(
            checks.TrueForAll(check => check.Passes),
            ProviderTransferBudget.AllocationRatioTolerance,
            checks);
    }

    /// <summary>
    /// The configuration the reference was taken under. These exist for one
    /// reason: a ratio only means something when the two runs measured the same
    /// message, at the same offered load, over the same bytes.
    /// </summary>
    private static void AddConfiguration(
        List<GateCheck> checks,
        ProviderTransferCellBaseline cell,
        ProviderTransferOutcome outcome)
    {
        AddExact(checks, "referência: bytes por anexo", cell.AttachmentBytes, outcome.AttachmentBytes);
        AddExact(checks, "referência: anexos por mensagem", cell.AttachmentCount, outcome.AttachmentCount);
        AddBoolean(
            checks,
            "referência: forma do conteúdo",
            string.Equals(cell.ContentShape, outcome.ContentShape, StringComparison.Ordinal));
        AddExact(checks, "referência: bytes por bloco da fonte", cell.SourceChunkBytes, outcome.SourceChunkBytes);
        AddExact(checks, "referência: operações por braço", cell.OperationsPerArm, outcome.OperationsPerArm);
        AddBoolean(
            checks,
            "referência: comprimento declarado no corpo",
            cell.ContentLengthDeclared == outcome.ContentLengthDeclared);
        AddBoolean(
            checks,
            "referência: digest do corpus",
            string.Equals(cell.SourceContentSha256, outcome.SourceContentSha256, StringComparison.Ordinal));
        AddExact(checks, "referência: bytes do corpo composto", cell.BodyBytes, outcome.BodyBytes);
    }

    /// <summary>
    /// The reference and the run under the same collector, heap count included.
    /// Recording under one and comparing under another is the divergence that
    /// nothing else in the run would report.
    /// </summary>
    private static void AddCollector(
        List<GateCheck> checks,
        ProviderTransferBaseline baseline,
        ProviderTransferOutcome outcome)
    {
        AddBoolean(
            checks,
            "referência: modo do coletor",
            baseline.ServerGarbageCollection == outcome.ServerGarbageCollection);
        AddExact(
            checks,
            "referência: heaps do coletor",
            baseline.GarbageCollectorHeapCount,
            outcome.GarbageCollectorHeapCount);
    }

    /// <summary>
    /// The one ratio this bench can carry: allocation per send against the
    /// buffering arm of the same run, compared with the ratio the reference
    /// recorded. The buffering arm is not graded against itself, because that
    /// ratio is one on both sides and would pass whatever happened.
    /// <para>
    /// Throughput, the largest latency and the sampled peak of the heap are
    /// recorded and not graded, and the reason is measured. Across five
    /// isolated runs of the same cell the throughput ratio moved by a factor
    /// of 19,6, the ratio of the largest sample by 67,7 and the ratio of the
    /// peak heap by 8,1. A band that admits those runs cannot refuse a
    /// regression, and a band that refuses a regression would fail honest
    /// runs; either way it is not a check. The peak of the working set is
    /// steady, with a spread of 1,20, and it is left out for the opposite
    /// reason: healthy runs already reach 1,06 of the buffering arm and a
    /// candidate that held the whole message would reach about the same, so
    /// the quantity does not separate the two.
    /// </para>
    /// <para>
    /// The tail is read as the largest sample observed and never as the
    /// ninety-ninth percentile, which below a thousand samples is the largest
    /// sample under a name it did not earn.
    /// </para>
    /// </summary>
    private static void AddRatios(
        List<GateCheck> checks,
        ProviderTransferCellBaseline cell,
        ProviderTransferOutcome outcome)
    {
        ProviderTransferArm buffer = ArmOf(outcome, ProviderTransferArms.BufferArm);
        foreach (var armId in ProviderTransferArms.All.Where(arm =>
            !string.Equals(arm, ProviderTransferArms.BufferArm, StringComparison.Ordinal)))
        {
            ProviderTransferArm arm = ArmOf(outcome, armId);
            ProviderTransferArmBaseline reference = ArmOf(cell, armId);
            AddCeiling(
                checks,
                $"{armId}: razão de alocação contra buffer",
                Ratio(arm.AllocatedBytesPerOperation, buffer.AllocatedBytesPerOperation),
                reference.AllocationRatio * (1 + ProviderTransferBudget.AllocationRatioTolerance));
        }
    }

    private static ProviderTransferArm ArmOf(ProviderTransferOutcome outcome, string armId)
        => outcome.Arms.FirstOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"A rodada não mediu o braço {armId}.");

    private static ProviderTransferArmBaseline ArmOf(ProviderTransferCellBaseline cell, string armId)
        => cell.Arms.FirstOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"A referência não registrou o braço {armId}.");

    private static double Ratio(double numerator, double denominator)
        => denominator > 0 ? numerator / denominator : double.NaN;

    private static void AddExact(List<GateCheck> checks, string metric, double expected, double measured)
        => checks.Add(new GateCheck(metric, expected, measured, expected, measured.Equals(expected)));

    private static void AddCeiling(List<GateCheck> checks, string metric, double measured, double limit)
        => checks.Add(new GateCheck(metric, limit, measured, limit, measured <= limit));

    private static void AddFloor(List<GateCheck> checks, string metric, double measured, double limit)
        => checks.Add(new GateCheck(metric, limit, measured, limit, measured >= limit));

    private static void AddBoolean(List<GateCheck> checks, string metric, bool passes)
        => checks.Add(new GateCheck(metric, 1, passes ? 1 : 0, 1, passes));
}
