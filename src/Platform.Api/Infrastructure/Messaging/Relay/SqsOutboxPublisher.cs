using System.Text.Json;
using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Publishes claimed rows to SQS with <c>SendMessageBatch</c> in chunks of
/// ten, chunk windows bounded by the configured concurrency. The message body
/// is the stored payload text exactly as the database returned it, never
/// re-serialized; <c>message_key</c>, <c>event_type</c> and the stored
/// headers travel as message attributes. A destination without a queue fails
/// whole: nothing is created, nothing is dropped, every row stays pending.
/// </summary>
internal sealed class SqsOutboxPublisher(
    IAmazonSQS sqs,
    SqsQueueUrlResolver queueUrlResolver,
    IOptions<OutboxRelayOptions> options,
    OutboxRelayHealthState healthState,
    TimeProvider timeProvider,
    ILogger<SqsOutboxPublisher> logger) : IOutboxPublisher
{
    private const int SqsBatchSize = 10;
    private const string MessageKeyAttribute = "messageKey";
    private const string EventTypeAttribute = "eventType";

    public async Task<OutboxPublishOutcome> PublishAsync(
        IReadOnlyList<PendingOutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        var accepted = new List<Guid>();
        var failures = new List<OutboxPublishFailure>();

        foreach (IGrouping<string, PendingOutboxMessage> group in
            messages.GroupBy(message => message.Destination, StringComparer.Ordinal))
        {
            PendingOutboxMessage[] pending = [.. group];
            string? queueUrl;
            try
            {
                queueUrl = await queueUrlResolver.ResolveAsync(group.Key, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.QueueResolutionFailed(group.Key, pending.Length, exception);
                failures.AddRange(pending.Select(message =>
                    new OutboxPublishFailure(message.Id, group.Key, "queue-resolution-failed")));
                continue;
            }

            if (queueUrl is null)
            {
                healthState.ReportQueueMissing(group.Key, timeProvider.GetUtcNow());
                logger.QueueMissing(group.Key, pending.Length);
                failures.AddRange(pending.Select(message =>
                    new OutboxPublishFailure(message.Id, group.Key, "queue-missing")));
                continue;
            }

            healthState.ReportQueueAvailable(group.Key);

            PendingOutboxMessage[][] chunks = [.. pending.Chunk(SqsBatchSize)];
            foreach (PendingOutboxMessage[][] window in chunks.Chunk(options.Value.PublishConcurrency))
            {
                ChunkResult[] results = await Task.WhenAll(
                    window.Select(chunk => SendChunkAsync(queueUrl, group.Key, chunk, cancellationToken)));
                foreach (ChunkResult result in results)
                {
                    accepted.AddRange(result.Accepted);
                    failures.AddRange(result.Failures);
                }
            }
        }

        return new OutboxPublishOutcome { AcceptedIds = accepted, Failures = failures };
    }

    private async Task<ChunkResult> SendChunkAsync(
        string queueUrl,
        string destination,
        PendingOutboxMessage[] chunk,
        CancellationToken cancellationToken)
    {
        var request = new SendMessageBatchRequest
        {
            QueueUrl = queueUrl,
            Entries = [.. chunk.Select(BuildEntry)],
        };

        try
        {
            SendMessageBatchResponse response = await sqs.SendMessageBatchAsync(request, cancellationToken);
            List<Guid> accepted = [.. (response.Successful ?? [])
                .Select(entry => Guid.ParseExact(entry.Id, "N"))];
            List<OutboxPublishFailure> failures = [.. (response.Failed ?? [])
                .Select(entry => new OutboxPublishFailure(
                    Guid.ParseExact(entry.Id, "N"),
                    destination,
                    $"{entry.Code}: {entry.Message}"))];
            if (failures.Count > 0)
            {
                logger.BatchEntriesRejected(destination, failures.Count, failures[0].Reason);
            }

            return new ChunkResult(accepted, failures);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Transport failure: every row of the chunk stays pending and the
            // next pass retries; the message may still have reached the queue
            // (at-least-once, dedupe belongs to the consumer).
            logger.PublishCallFailed(destination, chunk.Length, exception);
            return new ChunkResult(
                [],
                [.. chunk.Select(message =>
                    new OutboxPublishFailure(message.Id, destination, exception.GetType().Name))]);
        }
    }

    private static SendMessageBatchRequestEntry BuildEntry(PendingOutboxMessage message)
    {
        var attributes = new Dictionary<string, MessageAttributeValue>(StringComparer.Ordinal)
        {
            [MessageKeyAttribute] = StringAttribute(message.MessageKey),
            [EventTypeAttribute] = StringAttribute(message.EventType),
        };

        using var headers = JsonDocument.Parse(message.HeadersJson);
        if (headers.RootElement.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty header in headers.RootElement.EnumerateObject())
            {
                if (header.Value.ValueKind is JsonValueKind.String
                    && header.Value.GetString() is { Length: > 0 } value
                    && !attributes.ContainsKey(header.Name))
                {
                    attributes[header.Name] = StringAttribute(value);
                }
            }
        }

        return new SendMessageBatchRequestEntry
        {
            Id = message.Id.ToString("N"),
            // The stored jsonb payload text, byte for byte; the envelope was
            // written by the producer and the relay never re-wraps it.
            MessageBody = message.PayloadJson,
            MessageAttributes = attributes,
        };
    }

    private static MessageAttributeValue StringAttribute(string value)
        => new() { DataType = "String", StringValue = value };

    private sealed record ChunkResult(
        IReadOnlyList<Guid> Accepted,
        IReadOnlyList<OutboxPublishFailure> Failures);
}
