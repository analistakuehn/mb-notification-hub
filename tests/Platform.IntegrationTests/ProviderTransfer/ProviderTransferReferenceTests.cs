using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// Whether each comparison against the versioned reference can fail, one at a
/// time. The run is measured once and then each field of it is moved on its
/// own, so a check that stayed green under the move it is supposed to catch is
/// reported here and not discovered by a regression that walked past it.
/// </summary>
[Collection(ProviderTransferMeasurementCollectionDefinition.Name)]
public sealed class ProviderTransferReferenceTests
{
    private static async Task<ProviderTransferOutcome> MeasureAsync()
    {
        ProviderTransferOutcome measured = await ProviderTransferScenario.RunAsync(
            new ProviderTransferProfile(
                ProviderTransferProfiles.Floor,

                // The floor of the ratified envelope and not something smaller:
                // below it the buffering arm allocates about what the
                // incremental one does, the ratio has no headroom, and a
                // candidate that regressed all the way to buffering would still
                // sit inside the band.
                ProviderTransferProfiles.Of(ProviderTransferProfiles.Floor).AttachmentBytes,
                1,
                AttachmentContentShape.Readable,
                16 * 1_024,
                TimeSpan.Zero,
                2,
                1,
                DeclareContentLength: true),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);

        // The host the tests run on is not the container the budget describes,
        // so the run is stated as if it were: what this class measures is the
        // comparison against the reference, not the collector of the host.
        return measured with
        {
            ServerGarbageCollection = true,
            GarbageCollectorHeapCount = 1,
            OperationsPerArm = ProviderTransferBudget.MinimumSamplesPerArm,
        };
    }

    private static ProviderTransferBaseline ReferenceOf(ProviderTransferOutcome outcome)
        => ProviderTransferBaseline.From([outcome, outcome, outcome], ["a", "b", "c"], "host");

    private static IReadOnlyList<string> Failing(
        ProviderTransferBaseline reference,
        ProviderTransferOutcome outcome)
        => [.. ProviderTransferGate
            .Evaluate(reference, outcome, ProviderTransferArms.StreamingArm)
            .Checks
            .Where(check => !check.Passes)
            .Select(check => check.Metric)];

    [Fact]
    public async Task A_run_that_matches_the_reference_in_every_field_passes_every_comparison()
    {
        ProviderTransferOutcome outcome = await MeasureAsync();

        GateOutcome gate = ProviderTransferGate.Evaluate(
            ReferenceOf(outcome), outcome, ProviderTransferArms.StreamingArm);

        gate.Passes.ShouldBeTrue(string.Join("; ", Failing(ReferenceOf(outcome), outcome)));
    }

    public static TheoryData<string, string> Divergences()
        => new()
        {
            { "bytes por anexo", "referência: bytes por anexo" },
            { "anexos por mensagem", "referência: anexos por mensagem" },
            { "forma do conteúdo", "referência: forma do conteúdo" },
            { "bytes por bloco da fonte", "referência: bytes por bloco da fonte" },
            { "operações por braço", "referência: operações por braço" },
            { "comprimento declarado", "referência: comprimento declarado no corpo" },
            { "digest do corpus", "referência: digest do corpus" },
            { "bytes do corpo composto", "referência: bytes do corpo composto" },
            { "modo do coletor", "referência: modo do coletor" },
            { "heaps do coletor", "referência: heaps do coletor" },
        };

    [Theory]
    [MemberData(nameof(Divergences))]
    public async Task Each_field_that_moves_away_from_the_reference_turns_its_own_comparison_red(
        string moved,
        string metric)
    {
        ProviderTransferOutcome outcome = await MeasureAsync();
        ProviderTransferBaseline reference = ReferenceOf(outcome);
        ProviderTransferOutcome diverged = moved switch
        {
            "bytes por anexo" => outcome with { AttachmentBytes = outcome.AttachmentBytes + 3 },
            "anexos por mensagem" => outcome with { AttachmentCount = outcome.AttachmentCount + 1 },
            "forma do conteúdo" => outcome with
            {
                ContentShape = AttachmentContentShape.Escapable.ToString(),
            },
            "bytes por bloco da fonte" => outcome with { SourceChunkBytes = outcome.SourceChunkBytes / 2 },
            "operações por braço" => outcome with { OperationsPerArm = outcome.OperationsPerArm + 1 },
            "comprimento declarado" => outcome with { ContentLengthDeclared = false },
            "digest do corpus" => outcome with { SourceContentSha256 = new string('0', 64) },
            "bytes do corpo composto" => outcome with { BodyBytes = outcome.BodyBytes + 1 },
            "modo do coletor" => outcome with { ServerGarbageCollection = false },
            "heaps do coletor" => outcome with { GarbageCollectorHeapCount = 4 },
            _ => throw new InvalidOperationException($"Divergência não prevista: {moved}"),
        };

        Failing(reference, diverged).ShouldContain(metric);
    }

    [Fact]
    public async Task A_candidate_that_starts_allocating_like_the_buffering_arm_turns_the_ratio_red()
    {
        ProviderTransferOutcome outcome = await MeasureAsync();
        ProviderTransferBaseline reference = ReferenceOf(outcome);
        ProviderTransferArm buffer = outcome.Arms.Single(arm =>
            string.Equals(arm.ArmId, ProviderTransferArms.BufferArm, StringComparison.Ordinal));
        ProviderTransferOutcome regressed = outcome with
        {
            Arms =
            [
                .. outcome.Arms.Select(arm =>
                    string.Equals(arm.ArmId, ProviderTransferArms.StreamingArm, StringComparison.Ordinal)
                        ? arm with { AllocatedBytes = buffer.AllocatedBytes }
                        : arm),
            ],
        };

        Failing(reference, regressed).ShouldContain("streaming: razão de alocação contra buffer");
    }

    [Fact]
    public async Task A_run_of_a_cell_the_reference_never_recorded_is_refused_before_any_ratio()
    {
        ProviderTransferOutcome outcome = await MeasureAsync();
        ProviderTransferBaseline reference = ReferenceOf(outcome);

        IReadOnlyList<string> failing = Failing(
            reference, outcome with { ConfiguredConcurrency = outcome.ConfiguredConcurrency + 7 });

        failing.ShouldContain(metric => metric.StartsWith("referência: célula", StringComparison.Ordinal));
    }
}
