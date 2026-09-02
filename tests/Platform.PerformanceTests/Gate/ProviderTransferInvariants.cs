using System.Globalization;
using NotificationHub.PerformanceTests.Instrumentation;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>
/// What one run has to answer on its own, with no reference to compare
/// against. Two kinds of thing live here and they are not the same: whether the
/// arms did the same job at all, and whether the arm under judgement fits the
/// budget the deployment target implies. A run that fails the first compared
/// nothing; a run that fails the second compared honestly and lost.
/// <para>
/// Every check below has a red path a run can reach. A check whose value comes
/// from the configuration that produced it is not evidence, and the ones this
/// gate used to carry have been removed rather than kept as decoration.
/// </para>
/// </summary>
internal static class ProviderTransferInvariants
{
    /// <summary>The arm the budget is charged to; the others are the contrast.</summary>
    internal const string DefaultCandidate = ProviderTransferArms.StreamingArm;

    internal static IReadOnlyList<GateCheck> Checks(ProviderTransferOutcome outcome)
        => Checks(outcome, DefaultCandidate);

    internal static IReadOnlyList<GateCheck> Checks(ProviderTransferOutcome outcome, string candidateArm)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateArm);
        var checks = new List<GateCheck>();
        AddEnvelope(checks, outcome);
        AddCollector(checks, outcome);
        checks.AddRange(EquivalenceChecks(outcome));
        AddBudget(checks, outcome, candidateArm);
        return checks;
    }

    /// <summary>
    /// What a run has to satisfy before it can become a reference: it compared
    /// the same message, it sat inside the ratified envelope, and it ran under
    /// the collector the reference describes. The budget is deliberately not
    /// here. A reference records what a run measured; whether the candidate
    /// fits the budget is a verdict taken at every comparison from constants of
    /// this assembly, and a run that lost that verdict is still a faithful
    /// record of what it measured.
    /// </summary>
    internal static IReadOnlyList<GateCheck> RecordableChecks(ProviderTransferOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var checks = new List<GateCheck>();
        AddEnvelope(checks, outcome);
        AddCollector(checks, outcome);
        checks.AddRange(EquivalenceChecks(outcome));
        return checks;
    }

    /// <summary>
    /// Whether the arms did the same job at all: the same message, counted the
    /// same way, read back the same by the double, with nothing left behind.
    /// It is separate from the rest because a run can answer this on any host,
    /// while the envelope, the collector and the budget answer about the run
    /// this hub would grade.
    /// </summary>
    internal static IReadOnlyList<GateCheck> EquivalenceChecks(ProviderTransferOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        var checks = new List<GateCheck>();
        AddEquivalence(checks, outcome);
        foreach (ProviderTransferArm arm in outcome.Arms)
        {
            AddArm(checks, outcome, arm);
        }

        return checks;
    }

    /// <summary>The equivalence checks that failed, in the words they report with.</summary>
    internal static IEnumerable<string> Violations(ProviderTransferOutcome outcome)
        => Describe(EquivalenceChecks(outcome));

    /// <summary>Every check that failed, equivalence, envelope, collector and budget alike.</summary>
    internal static IEnumerable<string> Violations(ProviderTransferOutcome outcome, string candidateArm)
        => Describe(Checks(outcome, candidateArm));

    private static IEnumerable<string> Describe(IReadOnlyList<GateCheck> checks)
        => checks
            .Where(check => !check.Passes)
            .Select(check => string.Create(
                CultureInfo.InvariantCulture,
                $"{check.Metric}: medido {check.Measured:0.###}, limite {check.Limit:0.###}."));

    /// <summary>
    /// The ratified product envelope. It is a check and not a refusal so that a
    /// run outside it produces a red line instead of an exception: the numbers
    /// of such a run are still readable, they simply do not describe a message
    /// this hub is allowed to send.
    /// </summary>
    private static void AddEnvelope(List<GateCheck> checks, ProviderTransferOutcome outcome)
    {
        AddCeiling(
            checks,
            "envelope: anexos por mensagem",
            outcome.AttachmentCount,
            ProviderTransferBudget.MaxAttachmentsPerMessage);
        AddCeiling(
            checks,
            "envelope: bytes crus somados",
            outcome.TotalRawAttachmentBytes,
            ProviderTransferBudget.MaxTotalRawAttachmentBytes);
        AddFloor(
            checks,
            "amostras por braço",
            outcome.OperationsPerArm,
            ProviderTransferBudget.MinimumSamplesPerArm);
    }

    /// <summary>
    /// The collector the run happened under. The mode alone does not describe a
    /// configuration: the heap count moves the same measurement by more than
    /// most regressions this gate exists to catch, and the deployment target
    /// gives one processor and therefore one heap.
    /// </summary>
    private static void AddCollector(List<GateCheck> checks, ProviderTransferOutcome outcome)
    {
        AddBoolean(checks, "coletor: modo servidor", outcome.ServerGarbageCollection);
        AddExact(
            checks,
            "coletor: heaps pinados",
            CollectorPin.RatifiedHeapCount,
            outcome.GarbageCollectorHeapCount);
    }

    /// <summary>
    /// The set of arms is not checked here. Two guards before this one refuse a
    /// partial comparison, one when the command line is parsed and one when the
    /// reference is loaded, so a run with a missing arm never reaches the gate
    /// and the line would be green in every run that does. A check whose red
    /// path nothing can walk is decoration, and decoration is what this set was
    /// closed to remove.
    /// </summary>
    private static void AddEquivalence(List<GateCheck> checks, ProviderTransferOutcome outcome)
        => AddBoolean(checks, "corpo idêntico entre os braços", outcome.ArmsAgreeOnBody);

    private static void AddArm(List<GateCheck> checks, ProviderTransferOutcome outcome, ProviderTransferArm arm)
    {
        // The body the double counted against the arithmetic of the field. It
        // is the check the adversarial corpus exists for: with content whose
        // base64 is nothing but escapable characters, a field handed to the
        // JSON encoder measures six times what the arithmetic says, and with
        // any other content the two agree whichever call wrote it.
        AddExact(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{arm.ArmId}: corpo contra a aritmética do campo"),
            outcome.BodyBytes,
            arm.CapturedBodyBytes);

        // The declared length is not compared with the bytes received, and the
        // reason was measured rather than assumed. The transport already
        // enforces that equality: a body emitted shorter or longer than the
        // length it declared does not arrive with a mismatch, it fails as a
        // call. A mutation that declared one byte less than it wrote never
        // produced a red line, it produced a run with no captured call at all.
        // The claim that the anticipated length is exact is carried instead by
        // the check above, which compares what the double received with the
        // arithmetic the streaming arm declares.
        AddExact(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{arm.ArmId}: digests distintos do corpo"),
            1,
            arm.DistinctCapturedDigests);
        AddExact(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{arm.ArmId}: chamadas aceitas pelo provedor"),
            arm.Operations,
            arm.AcceptedCalls);
        AddExact(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{arm.ArmId}: chamadas vistas pelo provedor"),
            arm.Operations,
            arm.ProviderCalls);
        AddBoolean(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{arm.ArmId}: ida e volta de cada anexo"),
            arm.Attachments.Count == outcome.AttachmentCount
                && arm.Attachments.All(check =>
                    check.DigestMatchesSource
                    && check.MetadataMatches
                    && check.DecodedBytes == check.SourceBytes));
        AddBoolean(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{arm.ArmId}: limpeza sem resíduo"),
            arm.TemporaryFilesRemaining == 0 && arm.TemporaryRootRemoved && arm.OpenSourceStreams == 0);

        // Exactly the configured degree, not merely within it: a run that never
        // reached the parallelism it asked for measured another load.
        AddExact(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{arm.ArmId}: concorrência observada"),
            arm.ConfiguredConcurrency,
            arm.PeakConcurrency);
    }

    /// <summary>
    /// What the deployment target allows one send to cost, charged to the arm
    /// under judgement. Pointing the run at another arm is what falsifies these
    /// three: the buffering arm crosses the per-send budget at the ratified
    /// envelope, and the affine ceiling separates a cost that is fixed from one
    /// that follows the attachment, which a single number cannot do.
    /// </summary>
    private static void AddBudget(
        List<GateCheck> checks,
        ProviderTransferOutcome outcome,
        string candidateArm)
    {
        ProviderTransferArm? candidate = outcome.Arms.FirstOrDefault(arm =>
            string.Equals(arm.ArmId, candidateArm, StringComparison.Ordinal));
        if (candidate is null)
        {
            AddBoolean(
                checks,
                string.Create(CultureInfo.InvariantCulture, $"candidato {candidateArm} medido na rodada"),
                false);
            return;
        }

        AddCeiling(
            checks,
            string.Create(
                CultureInfo.InvariantCulture, $"{candidate.ArmId}: alocação por envio contra o orçamento"),
            candidate.AllocatedBytesPerOperation,
            ProviderTransferBudget.PerSendMemoryBudgetBytes);
        AddCeiling(
            checks,
            string.Create(CultureInfo.InvariantCulture, $"{candidate.ArmId}: alocação contra o teto afim"),
            candidate.AllocatedBytesPerOperation,
            ProviderTransferBudget.AllocationCeilingBytes(outcome.TotalRawAttachmentBytes));
        AddCeiling(
            checks,
            string.Create(
                CultureInfo.InvariantCulture, $"{candidate.ArmId}: coleções de geração 2 por operação"),
            candidate.Generation2CollectionsPerOperation,
            0);
        AddCeiling(
            checks,
            string.Create(
                CultureInfo.InvariantCulture, $"{candidate.ArmId}: pausa de coleta por operação, ms"),
            candidate.CollectionPauseMillisecondsPerOperation,
            0);
    }

    private static void AddExact(List<GateCheck> checks, string metric, double expected, double measured)
        => checks.Add(new GateCheck(metric, expected, measured, expected, measured.Equals(expected)));

    private static void AddCeiling(List<GateCheck> checks, string metric, double measured, double limit)
        => checks.Add(new GateCheck(metric, limit, measured, limit, measured <= limit));

    private static void AddFloor(List<GateCheck> checks, string metric, double measured, double limit)
        => checks.Add(new GateCheck(metric, limit, measured, limit, measured >= limit));

    private static void AddBoolean(List<GateCheck> checks, string metric, bool passes)
        => checks.Add(new GateCheck(metric, 1, passes ? 1 : 0, 1, passes));
}
