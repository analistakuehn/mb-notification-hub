using NotificationHub.PerformanceTests.Reporting;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>Compares method ratios from one run with ratios recorded by another run.</summary>
internal static class AttachmentTransferMethodGate
{
    internal static GateOutcome Evaluate(
        AttachmentTransferMethodBaseline baseline,
        AttachmentTransferOutcome outcome,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tolerance);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(tolerance, 1);

        var checks = new List<GateCheck>();
        AddExact(checks, "braços medidos", 3, outcome.Arms.Count);
        AddExact(checks, "bytes UTF-8 do payload", baseline.PayloadUtf8Bytes, outcome.PayloadUtf8Bytes);
        AddExact(checks, "bytes do envelope", baseline.EnvelopeBytes, outcome.EnvelopeBytes);
        AddExact(checks, "operações por braço", baseline.OperationsPerArm, outcome.OperationsPerArm);
        AddExact(checks, "concorrência configurada", baseline.ConfiguredConcurrency, outcome.ConfiguredConcurrency);
        AddBoolean(
            checks,
            "digest do corpus contra a referência",
            string.Equals(baseline.ExpectedDigest, outcome.ExpectedDigest, StringComparison.Ordinal));

        AttachmentTransferArm currentBuffer = ArmOf(outcome, AttachmentTransferMethodScenario.BufferArm);
        AttachmentTransferArmBaseline referenceBuffer = ArmOf(
            baseline,
            AttachmentTransferMethodScenario.BufferArm);
        foreach (var armId in RequiredArms())
        {
            AttachmentTransferArm current = ArmOf(outcome, armId);
            AttachmentTransferArmBaseline reference = ArmOf(baseline, armId);
            AddExact(checks, $"{armId}: bytes por operação", outcome.PayloadUtf8Bytes + outcome.EnvelopeBytes, current.BytesPerOperation);
            AddExact(checks, $"{armId}: operações", outcome.OperationsPerArm, current.Operations);
            AddExact(checks, $"{armId}: concorrência configurada", outcome.ConfiguredConcurrency, current.ConfiguredConcurrency);
            AddBoolean(checks, $"{armId}: concorrência observada", current.PeakConcurrency is > 0 && current.PeakConcurrency <= current.ConfiguredConcurrency);
            AddBoolean(checks, $"{armId}: igualdade de digest", current.DigestsEqual);
            AddExact(checks, $"{armId}: arquivos temporários residuais", 0, current.TemporaryFilesRemaining);
            AddBoolean(checks, $"{armId}: raiz temporária removida", current.TemporaryRootRemoved);

            AddUpperRatio(
                checks,
                $"{armId}: razão de alocação contra buffer",
                Ratio(reference.AllocatedBytesPerOperation, referenceBuffer.AllocatedBytesPerOperation),
                Ratio(current.AllocatedBytes / (double)current.Operations, currentBuffer.AllocatedBytes / (double)currentBuffer.Operations),
                tolerance);
            AddLowerRatio(
                checks,
                $"{armId}: razão de vazão contra buffer",
                Ratio(reference.ThroughputBytesPerSecond, referenceBuffer.ThroughputBytesPerSecond),
                Ratio(current.ThroughputBytesPerSecond, currentBuffer.ThroughputBytesPerSecond),
                tolerance);
        }

        AttachmentTransferArm spool = ArmOf(outcome, AttachmentTransferMethodScenario.SpoolArm);
        var expectedLogicalIo = checked((long)spool.BytesPerOperation * spool.Operations);
        AddExact(checks, "spool: bytes lógicos lidos", expectedLogicalIo, spool.LogicalFileReadBytes ?? -1);
        AddExact(checks, "spool: bytes lógicos escritos", expectedLogicalIo, spool.LogicalFileWrittenBytes ?? -1);
        AddExact(checks, "spool: temporários exercitados", spool.Operations, spool.TemporaryFilesCreated);

        return new GateOutcome(checks.TrueForAll(check => check.Passes), tolerance, checks);
    }

    private static IEnumerable<string> RequiredArms()
    {
        yield return AttachmentTransferMethodScenario.BufferArm;
        yield return AttachmentTransferMethodScenario.StreamingArm;
        yield return AttachmentTransferMethodScenario.SpoolArm;
    }

    private static AttachmentTransferArm ArmOf(AttachmentTransferOutcome outcome, string armId)
        => outcome.Arms.SingleOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"O portão exige o braço {armId} na mesma rodada; a rodada não o mediu.");

    private static AttachmentTransferArmBaseline ArmOf(
        AttachmentTransferMethodBaseline baseline,
        string armId)
        => baseline.Arms.SingleOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"A linha de base exige o braço {armId}; a referência não o contém.");

    private static double Ratio(double numerator, double denominator)
        => numerator >= 0 && denominator > 0 ? numerator / denominator : double.NaN;

    private static void AddUpperRatio(
        List<GateCheck> checks,
        string metric,
        double reference,
        double measured,
        double tolerance)
    {
        var limit = reference * (1 + tolerance);
        checks.Add(new GateCheck(metric, reference, measured, limit, double.IsFinite(measured) && measured <= limit));
    }

    private static void AddLowerRatio(
        List<GateCheck> checks,
        string metric,
        double reference,
        double measured,
        double tolerance)
    {
        var limit = reference * (1 - tolerance);
        checks.Add(new GateCheck(metric, reference, measured, limit, double.IsFinite(measured) && measured >= limit));
    }

    private static void AddExact(List<GateCheck> checks, string metric, double expected, double measured)
        => checks.Add(new GateCheck(metric, expected, measured, expected, measured.Equals(expected)));

    private static void AddBoolean(List<GateCheck> checks, string metric, bool passes)
        => checks.Add(new GateCheck(metric, 1, passes ? 1 : 0, 1, passes));
}
