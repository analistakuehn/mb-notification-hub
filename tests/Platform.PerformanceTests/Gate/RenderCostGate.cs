using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.PerformanceTests.Gate;

/// <summary>Compares one render run against its versioned baseline.</summary>
internal static class RenderCostGate
{
    /// <summary>
    /// Headroom on the allocation of one form. Bytes per operation are
    /// deterministic for a given runtime, so the allowance is not there to
    /// absorb noise: it exists so that a runtime upgrade resizing an internal
    /// buffer does not paint the gate red. A change of shape on this path moves
    /// the number by multiples, never by a tenth.
    /// </summary>
    private const double AllocationHeadroom = 0.10;

    /// <summary>
    /// Most of what a form allocated before its fields shared one context was
    /// the context itself, rebuilt per render. The share is measured in the
    /// same run against the arm that still builds one per render, so it needs
    /// no reference and no host: it is what fails, loudly, if the sharing is
    /// ever undone while the versioned number is regraded along with it.
    /// </summary>
    private const double SharedShareOfSeparate = 0.60;

    internal static GateOutcome Evaluate(RenderCostBaseline baseline, RenderCostOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(outcome);

        RenderCostArm shared = ArmOf(outcome, PublishedRenderCostScenario.SharedArm);
        RenderCostArm separate = ArmOf(outcome, PublishedRenderCostScenario.SeparateArm);
        var reference = baseline.BytesPerForm * (1 + AllocationHeadroom);
        var share = separate.BytesPerForm * SharedShareOfSeparate;
        GateCheck[] checks =
        [
            new GateCheck(
                "bytes por forma",
                baseline.BytesPerForm,
                shared.BytesPerForm,
                reference,
                shared.BytesPerForm <= reference),
            new GateCheck(
                "bytes por forma contra um contexto por render",
                separate.BytesPerForm,
                shared.BytesPerForm,
                share,
                shared.BytesPerForm <= share),
        ];
        return new GateOutcome(Array.TrueForAll(checks, check => check.Passes), AllocationHeadroom, checks);
    }

    private static RenderCostArm ArmOf(RenderCostOutcome outcome, string armId)
        => outcome.Arms.FirstOrDefault(arm => string.Equals(arm.ArmId, armId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"O portão exige o braço {armId} na mesma rodada; a rodada não o mediu.");
}
