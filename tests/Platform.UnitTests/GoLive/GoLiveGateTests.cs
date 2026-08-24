using NotificationHub.Platform.GoLiveChecks;

namespace NotificationHub.UnitTests.GoLive;

public sealed class GoLiveGateTests
{
    private static readonly DateTimeOffset CheckedAt = new(2026, 8, 24, 15, 30, 0, TimeSpan.Zero);
    private static readonly GoLiveVerifiedIdentity VerifiedGraphIdentity = new(
        new Guid("99cc1efd-3f10-43a9-bc5d-00e47fe0f347"),
        new Guid("fdc8b7f4-0956-478e-94d4-608d3f0ec244"),
        new Guid("738e728b-27e7-4ec9-a5d5-566acaf2022e"),
        new Guid("8dde5cb3-9a06-4e25-bf46-e70b1b5613f3"),
        MicrosoftGraphOperationalRoleSource.OperationalRole);

    [Fact]
    public async Task Zero_templates_and_zero_role_assignments_pass()
    {
        GoLiveGate gate = CreateGate(new StubSource(GoLiveSourceIdentifiers.TemplateManagement, 0),
            new StubSource(GoLiveSourceIdentifiers.MicrosoftGraph, 0, VerifiedGraphIdentity));

        GateRunResult result = await gate.RunAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(GoLiveExitCodes.Pass);
        result.Receipt.Status.ShouldBe(GoLiveStatuses.Pass);
        result.Receipt.Timestamp.ShouldBe("2026-08-24T15:30:00.0000000+00:00");
        result.Receipt.Sources.ShouldBe([
            new GoLiveSourceReceipt(GoLiveSourceIdentifiers.TemplateManagement, 0),
            new GoLiveSourceReceipt(
                GoLiveSourceIdentifiers.MicrosoftGraph,
                0,
                VerifiedGraphIdentity),
        ]);
        result.Receipt.Reasons.ShouldBeEmpty();
    }

    [Fact]
    public async Task Zero_graph_assignments_without_verified_identity_fails_closed()
    {
        GoLiveGate gate = CreateGate(new StubSource(GoLiveSourceIdentifiers.TemplateManagement, 0),
            new StubSource(GoLiveSourceIdentifiers.MicrosoftGraph, 0));

        GateRunResult result = await gate.RunAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(GoLiveExitCodes.Error);
        result.Receipt.Status.ShouldBe(GoLiveStatuses.Error);
        result.Receipt.Sources.Single(source =>
            source.Identifier == GoLiveSourceIdentifiers.MicrosoftGraph).Count.ShouldBeNull();
        result.Receipt.Reasons.ShouldBe([
            GoLiveReasons.SourceUnavailable(GoLiveSourceIdentifiers.MicrosoftGraph),
        ]);
    }

    [Fact]
    public async Task A_published_operational_template_fails_the_gate()
    {
        GoLiveGate gate = CreateGate(new StubSource(GoLiveSourceIdentifiers.TemplateManagement, 1),
            new StubSource(GoLiveSourceIdentifiers.MicrosoftGraph, 0, VerifiedGraphIdentity));

        GateRunResult result = await gate.RunAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(GoLiveExitCodes.Violation);
        result.Receipt.Status.ShouldBe(GoLiveStatuses.Fail);
        result.Receipt.Sources.Single(source =>
            source.Identifier == GoLiveSourceIdentifiers.TemplateManagement).Count.ShouldBe(1);
        result.Receipt.Reasons.ShouldBe([GoLiveReasons.PublishedOperationalTemplatesPresent]);
    }

    [Fact]
    public async Task An_operational_role_assignment_fails_the_gate()
    {
        GoLiveGate gate = CreateGate(new StubSource(GoLiveSourceIdentifiers.TemplateManagement, 0),
            new StubSource(GoLiveSourceIdentifiers.MicrosoftGraph, 2, VerifiedGraphIdentity));

        GateRunResult result = await gate.RunAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(GoLiveExitCodes.Violation);
        result.Receipt.Status.ShouldBe(GoLiveStatuses.Fail);
        result.Receipt.Sources.Single(source =>
            source.Identifier == GoLiveSourceIdentifiers.MicrosoftGraph).Count.ShouldBe(2);
        result.Receipt.Reasons.ShouldBe([GoLiveReasons.OperationalRoleAssignmentsPresent]);
    }

    [Fact]
    public async Task An_unavailable_template_source_fails_closed()
    {
        GoLiveGate gate = CreateGate(new ThrowingSource(GoLiveSourceIdentifiers.TemplateManagement),
            new StubSource(GoLiveSourceIdentifiers.MicrosoftGraph, 0, VerifiedGraphIdentity));

        GateRunResult result = await gate.RunAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(GoLiveExitCodes.Error);
        result.Receipt.Status.ShouldBe(GoLiveStatuses.Error);
        result.Receipt.Sources.Single(source =>
            source.Identifier == GoLiveSourceIdentifiers.TemplateManagement).Count.ShouldBeNull();
        result.Receipt.Reasons.ShouldBe([
            GoLiveReasons.SourceUnavailable(GoLiveSourceIdentifiers.TemplateManagement),
        ]);
    }

    [Fact]
    public async Task An_unavailable_graph_source_fails_closed()
    {
        GoLiveGate gate = CreateGate(new StubSource(GoLiveSourceIdentifiers.TemplateManagement, 0),
            new ThrowingSource(GoLiveSourceIdentifiers.MicrosoftGraph));

        GateRunResult result = await gate.RunAsync(CancellationToken.None);

        result.ExitCode.ShouldBe(GoLiveExitCodes.Error);
        result.Receipt.Status.ShouldBe(GoLiveStatuses.Error);
        result.Receipt.Sources.Single(source =>
            source.Identifier == GoLiveSourceIdentifiers.MicrosoftGraph).Count.ShouldBeNull();
        result.Receipt.Reasons.ShouldBe([
            GoLiveReasons.SourceUnavailable(GoLiveSourceIdentifiers.MicrosoftGraph),
        ]);
    }

    private static GoLiveGate CreateGate(IGoLiveCheckSource templates, IGoLiveCheckSource graph)
        => new(templates, graph, new FixedTimeProvider(CheckedAt));

    private sealed class StubSource(
        string identifier,
        int count,
        GoLiveVerifiedIdentity? verifiedIdentity = null) : IGoLiveCheckSource
    {
        public string Identifier => identifier;

        public ValueTask<GoLiveSourceCheck> CheckAsync(CancellationToken cancellationToken)
            => ValueTask.FromResult(new GoLiveSourceCheck(count, verifiedIdentity));
    }

    private sealed class ThrowingSource(string identifier) : IGoLiveCheckSource
    {
        public string Identifier => identifier;

        public ValueTask<GoLiveSourceCheck> CheckAsync(CancellationToken cancellationToken)
            => ValueTask.FromException<GoLiveSourceCheck>(
                new InvalidOperationException("Unavailable source with secret details."));
    }

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
