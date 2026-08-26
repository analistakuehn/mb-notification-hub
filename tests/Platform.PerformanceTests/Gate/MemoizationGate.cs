using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>Compares one memoization run against its versioned baseline.</summary>
internal static class MemoizationGate
{
    /// <summary>
    /// Headroom added to the contention limit, in disputes per thousand
    /// operations. A relative tolerance alone collapses when the reference is
    /// near zero, which is where a healthy policy sits: without this the guard
    /// would fail on one extra dispute in a million and get itself silenced.
    /// </summary>
    private const double ContentionHeadroom = 1.0;

    internal static GateOutcome Evaluate(
        MemoizationBaseline baseline,
        MemoizationOutcome outcome,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(outcome);

        MemoizationArm throughput = ArmOf(outcome, PublishedReadMemoizationScenario.ThroughputArm);
        MemoizationArm bound = ArmOf(outcome, PublishedReadMemoizationScenario.BoundArm);
        var measuredCost = MemoizationBaseline.MicrosecondsPerOperationOf(throughput);
        GateCheck[] checks =
        [
            Relative(
                "custo de uma operação em falta (us)",
                baseline.MicrosecondsPerOperation,
                measuredCost,
                tolerance),
            new GateCheck(
                "disputas de lock por mil operações",
                baseline.ContentionsPerThousand,
                throughput.ContentionsPerThousand,
                (baseline.ContentionsPerThousand * (1 + tolerance)) + ContentionHeadroom,
                throughput.ContentionsPerThousand
                    <= (baseline.ContentionsPerThousand * (1 + tolerance)) + ContentionHeadroom),

            // No reference and no tolerance: the budget is the policy's own
            // promise, and a resident set above it is memory the process never
            // gives back. Every in-process test passes while it grows.
            new GateCheck(
                "residente máximo sob escrita concorrente",
                bound.Ceiling,
                bound.ResidentMax,
                bound.Ceiling,
                bound.ResidentMax <= bound.Ceiling),
        ];
        return new GateOutcome(Array.TrueForAll(checks, check => check.Passes), tolerance, checks);
    }

    private static MemoizationArm ArmOf(MemoizationOutcome outcome, string armId)
        => outcome.Arms.FirstOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"O portão exige o braço {armId} na mesma rodada; a rodada não o mediu.");

    private static GateCheck Relative(string metric, double reference, double measured, double tolerance)
    {
        var limit = reference * (1 + tolerance);
        return new GateCheck(metric, reference, measured, limit, measured <= limit);
    }
}
