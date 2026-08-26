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
            new GateCheck("reparses do conjunto quente", 0, hot.Parses, 0, hot.Parses == 0),

            // And the same claim at the end of the run rather than during it:
            // every character the arm offered is still answerable.
            Whole(hot),
        ];
        return new GateOutcome(Array.TrueForAll(checks, check => check.Passes), tolerance, checks);
    }

    private static GateCheck Whole(ParseMemoizationArm hot)
    {
        var missing = hot.OfferedChars - hot.ResidentChars;
        return new GateCheck("caracteres do catálogo fora da memória", 0, missing, 0, missing <= 0);
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
