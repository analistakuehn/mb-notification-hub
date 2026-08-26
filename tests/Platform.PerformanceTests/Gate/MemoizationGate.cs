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

            // The budget is the policy's own promise, and a resident set that
            // climbs past it is memory the process never gives back: every
            // in-process test passes while it grows. The allowance is not a
            // fudge factor. Compaction is scheduled rather than synchronous, so
            // writers that clear the size check before it runs are all admitted,
            // and the overshoot is bounded by how many of them are in flight.
            // Anything beyond that is a policy that stopped bounding, which is
            // the failure this check exists for and which overshoots by orders
            // of magnitude, never by a handful.
            ResidentBound(bound),
        ];
        return new GateOutcome(Array.TrueForAll(checks, check => check.Passes), tolerance, checks);
    }

    private static GateCheck ResidentBound(MemoizationArm bound)
    {
        var limit = bound.Ceiling + bound.Workers;
        return new GateCheck(
            "residente máximo sob escrita concorrente",
            bound.Ceiling,
            bound.ResidentMax,
            limit,
            bound.ResidentMax <= limit);
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
