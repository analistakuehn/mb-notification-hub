using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>Counters of one relay pass over every configured band.</summary>
internal readonly record struct OutboxRelayPassResult(int Published, int Failed)
{
    public static OutboxRelayPassResult None { get; } = new(0, 0);
}

/// <summary>
/// One pass of the producer-side relay: drain each configured band in fixed
/// priority order and, inside a band, each transport lane on its own. Publish
/// every claimed batch and stamp <c>sent_at</c> only on the messages the
/// transport accepted. Failed rows are never touched: they stay pending,
/// observable through the health check, and a later pass retries them.
///
/// The lane is the unit that stops on failure, not the band: an unreachable
/// bus at the head of a band would otherwise hold back every queue row of the
/// same band, turning an integration outage into a delivery outage. Consuming,
/// deduplication and scheduling belong to the consumers, not here.
/// </summary>
internal sealed class OutboxRelay(
    IOutboxPendingStore store,
    IOutboxPublisherRegistry publishers,
    IOptions<OutboxRelayOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxRelay> logger)
{
    public async Task<OutboxRelayPassResult> RunPassAsync(CancellationToken cancellationToken)
    {
        var published = 0;
        var failed = 0;
        var transports = OutboxTransportLanes.Restrict(options.Value.Transports, publishers.Transports);

        foreach (OutboxBand band in OutboxBands.Restrict(options.Value.Bands))
        {
            foreach (var transport in transports)
            {
                (var lanePublished, var laneFailed) =
                    await DrainLaneAsync(band, transport, cancellationToken);
                published += lanePublished;
                failed += laneFailed;
            }
        }

        return new OutboxRelayPassResult(published, failed);
    }

    private async Task<(int Published, int Failed)> DrainLaneAsync(
        OutboxBand band,
        string transport,
        CancellationToken cancellationToken)
    {
        IOutboxPublisher publisher = publishers.Resolve(transport);
        var published = 0;
        var failed = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            await using IOutboxClaim claim =
                await store.ClaimAsync(band, transport, options.Value.BatchSize, cancellationToken);
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
                // Stop draining this lane: re-claiming would loop over the
                // same failing rows. Other lanes of the band keep draining and
                // the next pass retries these.
                LogPendingFailures(transport, claim.Messages, outcome.Failures);
                break;
            }

            if (claim.Messages.Count < options.Value.BatchSize)
            {
                break;
            }
        }

        return (published, failed);
    }

    private void LogPendingFailures(
        string transport,
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
            logger.MessagesLeftPending(
                transport, group.Key, group.Count(), oldestPendingSeconds, group.First().Reason);
        }
    }
}
