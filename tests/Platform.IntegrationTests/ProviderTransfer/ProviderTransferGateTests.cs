using NotificationHub.PerformanceTests.Gate;
using NotificationHub.PerformanceTests.ProviderTransfer;
using NotificationHub.PerformanceTests.Reporting;
using NotificationHub.PerformanceTests.Scenarios;

namespace NotificationHub.IntegrationTests.ProviderTransfer;

/// <summary>
/// Whether the closed set of checks can fail. A gate whose red path nothing can
/// reach is not protection, it is decoration, and the way to tell the two apart
/// is to reach the red path.
/// </summary>
[Collection(ProviderTransferMeasurementCollectionDefinition.Name)]
public sealed class ProviderTransferGateTests
{
    private static ProviderTransferProfile Profile(
        long attachmentBytes,
        int attachments = 1,
        int operations = 4,
        int concurrency = 1)
        => new(
            ProviderTransferProfiles.Custom,
            attachmentBytes,
            attachments,
            AttachmentContentShape.Readable,
            16 * 1_024,
            TimeSpan.Zero,
            operations,
            concurrency,
            DeclareContentLength: true);

    private static GateCheck Check(IReadOnlyList<GateCheck> checks, string metric)
        => checks.SingleOrDefault(check => string.Equals(check.Metric, metric, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"O portão não emitiu a verificação {metric}.");

    [Fact]
    public void Every_corpus_of_the_matrix_stays_inside_the_ratified_envelope()
    {
        foreach (var profileId in ProviderTransferProfiles.All)
        {
            ProviderTransferCorpus corpus = ProviderTransferProfiles.Of(profileId);

            corpus.AttachmentCount.ShouldBeLessThanOrEqualTo(
                ProviderTransferBudget.MaxAttachmentsPerMessage);
            (corpus.AttachmentBytes * corpus.AttachmentCount).ShouldBeLessThanOrEqualTo(
                ProviderTransferBudget.MaxTotalRawAttachmentBytes);
        }
    }

    [Fact]
    public void The_floor_and_the_single_maximum_are_far_enough_apart_to_show_a_slope()
    {
        var floor = ProviderTransferProfiles.Of(ProviderTransferProfiles.Floor).AttachmentBytes;
        var maximum = ProviderTransferProfiles.Of(ProviderTransferProfiles.MaxSingle).AttachmentBytes;

        (maximum / floor).ShouldBe(28);
    }

    [Fact]
    public void Fragmentation_carries_the_same_total_in_the_most_attachments_the_envelope_admits()
    {
        ProviderTransferCorpus fragmented = ProviderTransferProfiles.Of(ProviderTransferProfiles.Fragmented);
        ProviderTransferCorpus single = ProviderTransferProfiles.Of(ProviderTransferProfiles.MaxSingle);

        fragmented.AttachmentCount.ShouldBe(ProviderTransferBudget.MaxAttachmentsPerMessage);
        (single.AttachmentBytes - (fragmented.AttachmentBytes * fragmented.AttachmentCount))
            .ShouldBeLessThan(fragmented.AttachmentCount);
    }

    [Fact]
    public void The_adversarial_corpus_is_the_only_one_whose_content_the_encoder_would_escape()
    {
        ProviderTransferProfiles.All
            .Count(profileId => ProviderTransferProfiles.Of(profileId).ContentShape
                is AttachmentContentShape.Escapable)
            .ShouldBe(1);
    }

    [Fact]
    public async Task A_run_of_more_attachments_than_the_envelope_admits_turns_the_envelope_check_red()
    {
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(8 * 1_024, attachments: ProviderTransferBudget.MaxAttachmentsPerMessage + 1),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);

        IReadOnlyList<GateCheck> checks = ProviderTransferInvariants.Checks(
            outcome, ProviderTransferInvariants.DefaultCandidate);

        Check(checks, "envelope: anexos por mensagem").Passes.ShouldBeFalse();
        ProviderTransferInvariants.Violations(outcome).ShouldBeEmpty(
            "a rodada fora do envelope ainda comparou os mesmos bytes");
    }

    [Fact]
    public async Task A_run_of_more_bytes_than_the_envelope_admits_turns_the_size_check_red()
    {
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(ProviderTransferBudget.MaxTotalRawAttachmentBytes + 1, operations: 2),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);

        Check(
            ProviderTransferInvariants.Checks(outcome, ProviderTransferInvariants.DefaultCandidate),
            "envelope: bytes crus somados").Passes.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_of_fewer_samples_than_a_percentile_needs_turns_the_sample_check_red()
    {
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(8 * 1_024, operations: 4),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);

        Check(
            ProviderTransferInvariants.Checks(outcome, ProviderTransferInvariants.DefaultCandidate),
            "amostras por braço").Passes.ShouldBeFalse();
        outcome.Arms.ShouldAllBe(arm => arm.LatencyP99Milliseconds == null);
        outcome.Arms.ShouldAllBe(arm => arm.LatencyMaxMilliseconds >= arm.LatencyP95Milliseconds);
    }

    [Fact]
    public async Task The_budget_checks_grade_the_arm_they_are_pointed_at()
    {
        // The buffering arm holds the attachment and the whole message at once,
        // so its allocation follows the size of the attachment. Pointing the
        // budget at it is what shows the affine ceiling can refuse.
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(ProviderTransferProfiles.Of(ProviderTransferProfiles.Floor).AttachmentBytes),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);

        Check(
            ProviderTransferInvariants.Checks(outcome, ProviderTransferArms.StreamingArm),
            "streaming: alocação contra o teto afim").Passes.ShouldBeTrue();
        Check(
            ProviderTransferInvariants.Checks(outcome, ProviderTransferArms.BufferArm),
            "buffer: alocação contra o teto afim").Passes.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_whose_heap_count_is_not_the_pinned_one_turns_the_collector_check_red()
    {
        ProviderTransferOutcome measured = await ProviderTransferScenario.RunAsync(
            Profile(8 * 1_024, operations: 2),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);
        ProviderTransferOutcome pinned = measured with
        {
            ServerGarbageCollection = true,
            GarbageCollectorHeapCount = 1,
        };
        ProviderTransferOutcome elsewhere = pinned with { GarbageCollectorHeapCount = 4 };

        Check(
            ProviderTransferInvariants.Checks(pinned, ProviderTransferInvariants.DefaultCandidate),
            "coletor: heaps pinados").Passes.ShouldBeTrue();
        Check(
            ProviderTransferInvariants.Checks(elsewhere, ProviderTransferInvariants.DefaultCandidate),
            "coletor: heaps pinados").Passes.ShouldBeFalse();
        Check(
            ProviderTransferInvariants.Checks(
                pinned with { ServerGarbageCollection = false },
                ProviderTransferInvariants.DefaultCandidate),
            "coletor: modo servidor").Passes.ShouldBeFalse();
    }

    [Fact]
    public async Task A_reference_needs_more_than_one_run_of_a_cell_before_it_is_a_median()
    {
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(8 * 1_024, operations: 2),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);
        ProviderTransferOutcome recordable = outcome with
        {
            ServerGarbageCollection = true,
            GarbageCollectorHeapCount = 1,
            AttachmentCount = 1,
            OperationsPerArm = ProviderTransferBudget.MinimumSamplesPerArm,
        };

        InvalidOperationException thin = Should.Throw<InvalidOperationException>(
            () => ProviderTransferBaseline.From([recordable, recordable], ["a.json", "b.json"], "host"));

        thin.Message.ShouldContain("rodada");
        Should.NotThrow(() => ProviderTransferBaseline.From(
            [recordable, recordable, recordable], ["a.json", "b.json", "c.json"], "host"));
    }

    [Fact]
    public async Task A_reference_never_mixes_two_collectors()
    {
        ProviderTransferOutcome outcome = await ProviderTransferScenario.RunAsync(
            Profile(8 * 1_024, operations: 2),
            ProviderTransferArms.All,
            _ => { },
            CancellationToken.None);
        ProviderTransferOutcome recordable = outcome with
        {
            ServerGarbageCollection = true,
            GarbageCollectorHeapCount = 1,
            OperationsPerArm = ProviderTransferBudget.MinimumSamplesPerArm,
        };

        Should.Throw<InvalidOperationException>(() => ProviderTransferBaseline.From(
            [recordable, recordable, recordable with { GarbageCollectorHeapCount = 4 }],
            ["a.json", "b.json", "c.json"],
            "host"));
    }

    [Fact]
    public void The_budget_of_one_send_is_the_share_of_the_replica_divided_by_the_sends_in_flight()
    {
        ProviderTransferBudget.PerSendMemoryBudgetBytes.ShouldBe(26_214_400);
        ProviderTransferBudget.TransferPathMemoryBytes.ShouldBe(209_715_200);
        (ProviderTransferBudget.TransferPathMemoryBytes * 10)
            .ShouldBeLessThanOrEqualTo(ProviderTransferBudget.ReplicaMemoryBytes);
        ProviderTransferBudget.AllocationCeilingBytes(0)
            .ShouldBe(ProviderTransferBudget.AllocationConstantBytes);
        ProviderTransferBudget
            .AllocationCeilingBytes(ProviderTransferBudget.MaxTotalRawAttachmentBytes)
            .ShouldBeLessThan(ProviderTransferBudget.PerSendMemoryBudgetBytes);
    }
}
