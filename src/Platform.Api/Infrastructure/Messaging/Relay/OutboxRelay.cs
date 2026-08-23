using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>Counters of one relay pass over every configured band.</summary>
internal readonly record struct OutboxRelayPassResult(int Published, int Failed)
{
    public static OutboxRelayPassResult None { get; } = new(0, 0);
}

/// <summary>
/// One pass of the producer-side relay: drain each configured band in fixed
/// priority order, publish every claimed batch, and stamp <c>sent_at</c> only
/// on the messages the transport accepted. Failed rows are never touched:
/// they stay pending, observable through the health check, and a later pass
/// retries them. Consuming, deduplication and scheduling belong to the
/// consumers, not here.
/// </summary>
internal sealed class OutboxRelay(
    IOutboxPendingStore store,
    IOutboxPublisher publisher,
    IOptions<OutboxRelayOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger)
{
    public async Task<OutboxRelayPassResult> RunPassAsync(CancellationToken cancellationToken)
    {
        var published = 0;
        var failed = 0;

        foreach (OutboxBand band in OutboxBands.Restrict(options.Value.Bands))
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await using IOutboxClaim claim =
                    await store.ClaimAsync(band, options.Value.BatchSize, cancellationToken);
                if (claim.Messages.Count == 0)
                {
                    break;
                }

                OutboxPublishOutcome outcome = await publisher.PublishAsync(claim.Messages, cancellationToken);
                await claim.CompleteAsync(outcome.AcceptedIds, timeProvider.GetUtcNow(), cancellationToken);
                published += outcome.AcceptedIds.Count;
                failed += outcome.Failures.Count;

                if (outcome.Failures.Count > 0)
                {
                    // Stop draining this band: re-claiming would loop over the
                    // same failing rows. The next pass retries them.
                    LogPendingFailures(claim.Messages, outcome.Failures);
                    break;
                }

                if (claim.Messages.Count < options.Value.BatchSize)
                {
                    break;
                }
            }
        }

        return new OutboxRelayPassResult(published, failed);
    }

    private void LogPendingFailures(
        IReadOnlyList<PendingOutboxMessage> messages,
        IReadOnlyList<OutboxPublishFailure> failures)
    {
        Dictionary<Guid, PendingOutboxMessage> byId = messages.ToDictionary(message => message.Id);
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (IGrouping<string, OutboxPublishFailure> group in
            failures.GroupBy(failure => failure.Destination, StringComparer.Ordinal))
        {
            DateTimeOffset oldestCreatedAt = group
                .Select(failure => byId[failure.MessageId].CreatedAt)
                .Min();
            var oldestPendingSeconds = Math.Max(0, (now - oldestCreatedAt).TotalSeconds);
            logger.MessagesLeftPending(group.Key, group.Count(), oldestPendingSeconds, group.First().Reason);
        }
    }
}
