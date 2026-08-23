using NotificationHub.Api.Modules.Notifications.Features.Pipeline;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class NotificationPipelineTests
{
    private sealed class FixedStage(string name, StageOutcome outcome, string? reason = null) : INotificationStage
    {
        public int Executions { get; private set; }

        public string Name => name;

        public Task<StageOutcome> ExecuteAsync(NotificationContext context, CancellationToken cancellationToken)
        {
            Executions++;
            if (reason is not null)
            {
                context.LastReason = reason;
            }

            return Task.FromResult(outcome);
        }
    }

    [Fact]
    public async Task Every_stage_runs_in_order_and_the_commit_closes_the_run()
    {
        var first = new FixedStage("First", StageOutcome.Continue);
        var second = new FixedStage("Second", StageOutcome.Continue);
        var committer = new PipelineTestData.NoopCommitter();
        var context = new NotificationContext(
            PipelineTestData.AcceptedNotification(), Guid.NewGuid(), committer);

        PipelineCommitResult result = await new NotificationPipeline([first, second])
            .RunAsync(context, CancellationToken.None);

        first.Executions.ShouldBe(1);
        second.Executions.ShouldBe(1);
        committer.Committed.ShouldBeSameAs(context);
        result.ShouldBeOfType<PipelineCommitResult.Committed>();
        context.Trace.Entries.Select(entry => entry.Stage).ShouldBe(["First", "Second"]);
    }

    [Fact]
    public async Task A_rejection_stops_the_pipeline_and_still_commits()
    {
        var first = new FixedStage("First", StageOutcome.Reject, "no-consent");
        var second = new FixedStage("Second", StageOutcome.Continue);
        var committer = new PipelineTestData.NoopCommitter();
        var context = new NotificationContext(
            PipelineTestData.AcceptedNotification(), Guid.NewGuid(), committer);

        await new NotificationPipeline([first, second]).RunAsync(context, CancellationToken.None);

        second.Executions.ShouldBe(0);
        committer.Committed.ShouldBeSameAs(context);
        context.Trace.Entries.ShouldHaveSingleItem().Outcome.ShouldBe(StageOutcome.Reject);
        context.Trace.Entries[0].Reason.ShouldBe("no-consent");
    }

    [Fact]
    public async Task An_unexpected_exception_propagates_without_committing()
    {
        var throwing = new ThrowingStage();
        var committer = new PipelineTestData.NoopCommitter();
        var context = new NotificationContext(
            PipelineTestData.AcceptedNotification(), Guid.NewGuid(), committer);

        await Should.ThrowAsync<InvalidOperationException>(
            new NotificationPipeline([throwing]).RunAsync(context, CancellationToken.None));

        committer.Committed.ShouldBeNull();
    }

    private sealed class ThrowingStage : INotificationStage
    {
        public string Name => "Throwing";

        public Task<StageOutcome> ExecuteAsync(NotificationContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("falha técnica");
    }
}
