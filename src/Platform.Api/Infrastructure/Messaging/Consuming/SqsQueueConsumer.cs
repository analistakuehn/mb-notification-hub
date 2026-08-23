using Amazon.SQS;
using Amazon.SQS.Model;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>One queue a consuming role drains, with its slot-allocation rank (zero drains first under contention).</summary>
public sealed record SqsQueueBinding(string QueueName, int PriorityRank);

/// <summary>Counters of one receive pass over one queue.</summary>
public sealed record SqsConsumePassResult(int Received, int Processed, int Duplicates, int Discarded, int Failed)
{
    public static readonly SqsConsumePassResult None = new(0, 0, 0, 0, 0);
}

/// <summary>
/// The platform SQS consumer for one queue: long polling, bounded batch,
/// concurrency through the shared slot allocator, selective delete of the
/// settled messages only. A transient failure extends the message's
/// visibility with exponential backoff and jitter, so it returns to the
/// queue; the DLQ is reached exclusively through the redrive policy. A
/// permanently invalid message is recorded through the poison sink before the
/// delete, so nothing is ever dropped silently.
/// </summary>
internal sealed class SqsQueueConsumer<TProcessor>(
    SqsQueueBinding binding,
    IAmazonSQS sqs,
    SqsQueueUrlResolver queueUrlResolver,
    PrioritySlotAllocator slots,
    IServiceScopeFactory scopeFactory,
    IOptions<SqsConsumerOptions> options,
    TimeProvider timeProvider,
    ILogger logger)
    where TProcessor : ISqsMessageProcessor
{
    public string QueueName => binding.QueueName;

    /// <summary>Runs one receive-and-settle pass and returns its counters.</summary>
    public async Task<SqsConsumePassResult> RunPassAsync(CancellationToken cancellationToken)
    {
        var queueUrl = await queueUrlResolver.ResolveAsync(binding.QueueName, cancellationToken);
        if (queueUrl is null)
        {
            logger.ConsumerQueueMissing(binding.QueueName);
            return SqsConsumePassResult.None;
        }

        ReceiveMessageResponse response = await sqs.ReceiveMessageAsync(
            new ReceiveMessageRequest
            {
                QueueUrl = queueUrl,
                MaxNumberOfMessages = options.Value.BatchSize,
                WaitTimeSeconds = options.Value.WaitTimeSeconds,
                MessageAttributeNames = ["All"],
                MessageSystemAttributeNames = [MessageSystemAttributeName.ApproximateReceiveCount.Value],
            },
            cancellationToken);

        Message[] messages = [.. response.Messages ?? []];
        if (messages.Length == 0)
        {
            return SqsConsumePassResult.None;
        }

        SettleResult[] settled = await Task.WhenAll(
            messages.Select(message => SettleAsync(queueUrl, message, cancellationToken)));
        return new SqsConsumePassResult(
            messages.Length,
            settled.Count(result => result == SettleResult.Processed),
            settled.Count(result => result == SettleResult.Duplicate),
            settled.Count(result => result == SettleResult.Discarded),
            settled.Count(result => result == SettleResult.Failed));
    }

    private async Task<SettleResult> SettleAsync(
        string queueUrl,
        Message message,
        CancellationToken cancellationToken)
    {
        using IDisposable slot = await slots.AcquireAsync(binding.PriorityRank, cancellationToken);
        try
        {
            return await ProcessOneAsync(queueUrl, message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Transient by contract: the message returns with backoff and the
            // redrive policy owns the path to the DLQ.
            await ReturnWithBackoffAsync(queueUrl, message, exception, cancellationToken);
            return SettleResult.Failed;
        }
    }

    private async Task<SettleResult> ProcessOneAsync(
        string queueUrl,
        Message message,
        CancellationToken cancellationToken)
    {
        MessageEnvelopeParse parse = MessageEnvelopeParser.Parse(message.Body);
        if (parse.Envelope is not { } envelope)
        {
            await DiscardAsync(
                queueUrl,
                message,
                new PoisonMessage
                {
                    QueueName = binding.QueueName,
                    SqsMessageId = message.MessageId,
                    Reason = parse.InvalidReason!,
                },
                cancellationToken);
            return SettleResult.Discarded;
        }

        using IServiceScope scope = scopeFactory.CreateScope();
        TProcessor processor = scope.ServiceProvider.GetRequiredService<TProcessor>();
        if (!processor.Accepts(envelope.Type, envelope.SchemaVersion))
        {
            await DiscardAsync(
                queueUrl,
                message,
                new PoisonMessage
                {
                    QueueName = binding.QueueName,
                    SqsMessageId = message.MessageId,
                    EnvelopeMessageId = envelope.MessageId,
                    EventType = envelope.Type,
                    SchemaVersion = envelope.SchemaVersion,
                    Reason = "message-type-not-supported",
                },
                cancellationToken);
            return SettleResult.Discarded;
        }

        MessageDisposition disposition = await processor.ProcessAsync(envelope, cancellationToken);
        switch (disposition)
        {
            case MessageDisposition.Processed:
                await DeleteAsync(queueUrl, message, cancellationToken);
                return SettleResult.Processed;
            case MessageDisposition.Duplicate:
                logger.ConsumerDuplicateDetected(binding.QueueName, envelope.Type, envelope.MessageId);
                await DeleteAsync(queueUrl, message, cancellationToken);
                return SettleResult.Duplicate;
            case MessageDisposition.Discard discard:
                await DiscardAsync(
                    queueUrl,
                    message,
                    new PoisonMessage
                    {
                        QueueName = binding.QueueName,
                        SqsMessageId = message.MessageId,
                        EnvelopeMessageId = envelope.MessageId,
                        EventType = envelope.Type,
                        SchemaVersion = envelope.SchemaVersion,
                        Reason = discard.Reason,
                    },
                    cancellationToken);
                return SettleResult.Discarded;
            default:
                throw new InvalidOperationException(
                    $"Disposição de mensagem não suportada: {disposition.GetType().Name}.");
        }
    }

    /// <summary>
    /// The poison sink commits the discard trail and the processed mark
    /// before the delete: a crash in between redelivers the message, whose
    /// mark then resolves it as a duplicate discard, never as a silent drop.
    /// </summary>
    private async Task DiscardAsync(
        string queueUrl,
        Message message,
        PoisonMessage poison,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        IPoisonMessageSink sink = scope.ServiceProvider.GetRequiredService<IPoisonMessageSink>();
        await sink.RecordDiscardAsync(poison, cancellationToken);
        logger.ConsumerMessageDiscarded(binding.QueueName, poison.Reason, message.MessageId);
        await DeleteAsync(queueUrl, message, cancellationToken);
    }

    private async Task DeleteAsync(string queueUrl, Message message, CancellationToken cancellationToken)
        => await sqs.DeleteMessageAsync(
            new DeleteMessageRequest { QueueUrl = queueUrl, ReceiptHandle = message.ReceiptHandle },
            cancellationToken);

    private async Task ReturnWithBackoffAsync(
        string queueUrl,
        Message message,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var receiveCount = ReadReceiveCount(message);
        var delaySeconds = SqsBackoff.DelaySeconds(
            receiveCount, options.Value.BackoffBaseSeconds, options.Value.BackoffMaxSeconds);
        logger.ConsumerMessageFailedTransiently(
            binding.QueueName, message.MessageId, receiveCount, delaySeconds, exception);
        try
        {
            await sqs.ChangeMessageVisibilityAsync(
                new ChangeMessageVisibilityRequest
                {
                    QueueUrl = queueUrl,
                    ReceiptHandle = message.ReceiptHandle,
                    VisibilityTimeout = delaySeconds,
                },
                cancellationToken);
        }
        catch (Exception visibilityFailure) when (visibilityFailure is not OperationCanceledException)
        {
            // The original visibility timeout still returns the message.
            logger.ConsumerVisibilityChangeFailed(
                binding.QueueName, message.MessageId, timeProvider.GetUtcNow(), visibilityFailure);
        }
    }

    private static int ReadReceiveCount(Message message)
        => message.Attributes is { } attributes
            && attributes.TryGetValue(nameof(MessageSystemAttributeName.ApproximateReceiveCount), out var raw)
            && int.TryParse(raw, out var count)
                ? count
                : 1;

    private enum SettleResult
    {
        Processed,
        Duplicate,
        Discarded,
        Failed,
    }
}
