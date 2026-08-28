using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>Compares one parse memoization run against its versioned baseline.</summary>
internal static class ParseMemoizationGate
{
    /// <summary>
    /// Headroom added to the contention limit, in disputes per thousand
    /// lookups. A relative tolerance alone collapses when the reference is near
    /// zero, which is where a healthy policy sits: without this the guard would
    /// fail on one extra dispute in a million and get itself silenced.
    /// </summary>
    private const double ContentionHeadroom = 1.0;

    /// <summary>
    /// Spread the cost limit carries on top of the caller's tolerance. It is
    /// measured, not picked: honest medians of this arm on one mobile part ran
    /// from 0.044 to 0.091 microseconds inside a single evening, a factor of
    /// two, because the package scales its own frequency down as the arm heats
    /// it. The tolerance alone would put the limit inside that spread, which is
    /// how a guard fabricates a failure and gets itself silenced.
    /// <para>
    /// The floor that keeps it useful is the distance to the known failure: the
    /// policy that bounds the memoization by counting entries measures 5.9
    /// microseconds on the same arm, some ninety times the reference, so a limit
    /// at three times it still sits an order of magnitude below anything a real
    /// regression produces. The two checks below carry the exact signal; this
    /// one is the loose net around them.
    /// </para>
    /// </summary>
    private const double HostSpread = 2.0;

    internal static GateOutcome Evaluate(
        ParseMemoizationBaseline baseline,
        ParseMemoizationOutcome outcome,
        double tolerance)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(outcome);

        ParseMemoizationArm hot = ArmOf(outcome, ScribanParseMemoizationScenario.HotArm);
        ParseMemoizationArm loaded = ArmOf(outcome, ScribanParseMemoizationScenario.LoadedArm);
        var contentionLimit = (baseline.ContentionsPerThousand * (1 + tolerance)) + ContentionHeadroom;
        GateCheck[] checks =
        [
            Cost(
                baseline.MicrosecondsPerOperation,
                ParseMemoizationBaseline.MicrosecondsPerOperationOf(hot),
                tolerance),
            new GateCheck(
                "disputas de lock por mil buscas",
                baseline.ContentionsPerThousand,
                hot.ContentionsPerThousand,
                contentionLimit,
                hot.ContentionsPerThousand <= contentionLimit),

            // The arm offers nothing the budget cannot hold, so a source parsed
            // during it is a source the policy threw away while it was being
            // read. Zero is not a threshold to tune: one reparse here means the
            // catalogue no longer survives its own traffic, and the cost of that
            // is measured in whole passes, never in a lookup or two.
            Reparses(hot),

            // And the same claim at the end of the run rather than during it:
            // every source the arm offered is still answerable.
            Whole(hot),

            // The second arm reads the same catalogue against a budget that is
            // already full, with one source heavier than a compaction pass in
            // it. A policy that answers a refusal by freeing a fixed share of
            // the budget never admits that source again, and the arm then pays
            // its parse on every visit.
            Reparses(loaded),
            Heavy(loaded),
        ];
        return new GateOutcome(Array.TrueForAll(checks, check => check.Passes), tolerance, checks);
    }

    private static GateCheck Reparses(ParseMemoizationArm arm)
        => new($"reparses do conjunto quente ({arm.ArmId})", 0, arm.Parses, 0, arm.Parses == 0);

    private static GateCheck Whole(ParseMemoizationArm arm)
    {
        var missing = arm.Sources - arm.ResidentEntries;
        return new GateCheck($"fontes do catálogo fora da memória ({arm.ArmId})", 0, missing, 0, missing <= 0);
    }

    /// <summary>
    /// Whether the heavy source answered from memory at the end of the run. The
    /// entry count cannot carry this one: the ballast that loads the budget
    /// pads it, so the single source the arm exists for could go missing
    /// without moving the total by enough to notice.
    /// </summary>
    private static GateCheck Heavy(ParseMemoizationArm arm)
    {
        var answered = arm.LargeSourceHits ?? 0;
        return new GateCheck(
            $"fonte pesada respondida de memória ({arm.ArmId})", 1, answered, 1, answered >= 1);
    }

    private static ParseMemoizationArm ArmOf(ParseMemoizationOutcome outcome, string armId)
        => outcome.Arms.FirstOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"O portão exige o braço {armId} na mesma rodada; a rodada não o mediu.");

    private static GateCheck Cost(double reference, double measured, double tolerance)
    {
        var limit = reference * (1 + tolerance) * HostSpread;
        return new GateCheck("custo de uma busca quente (us)", reference, measured, limit, measured <= limit);
    }
}
