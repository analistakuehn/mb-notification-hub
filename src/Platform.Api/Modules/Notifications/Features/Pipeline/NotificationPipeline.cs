namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline;

/// <summary>
/// The Core pipeline: a mutable context crosses an ordered stage list and one
/// commit writes the result. Exceptions are never handled here: they
/// propagate, the message returns to the queue with backoff, and only the
/// redrive policy reaches the DLQ, so a technical failure can never surface
/// as a rejected notification in the trail.
/// </summary>
public sealed class NotificationPipeline(IReadOnlyList<INotificationStage> stages)
{
    public async Task<PipelineCommitResult> RunAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        foreach (INotificationStage stage in stages)
        {
            StageOutcome outcome = await stage.ExecuteAsync(context, cancellationToken);
            context.Trace.Add(stage.Name, outcome, context.LastReason);
            if (outcome != StageOutcome.Continue)
            {
                break;
            }
        }

        return await context.CommitAsync(cancellationToken);
    }
}
