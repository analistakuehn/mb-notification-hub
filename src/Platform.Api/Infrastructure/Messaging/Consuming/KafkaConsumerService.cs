using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>Topics one consuming role reads, under the consumer group that identifies it.</summary>
internal sealed record KafkaConsumerPlan<TProcessor>(string GroupId, IReadOnlyList<string> Topics)
    where TProcessor : IKafkaMessageProcessor;

/// <summary>Counters of one consume pass.</summary>
internal readonly record struct KafkaConsumePassResult(
    int Received,
    int Processed,
    int Duplicates,
    int DeadLettered,
    int Paused)
{
    public static KafkaConsumePassResult None { get; } = new(0, 0, 0, 0, 0);
}

/// <summary>
/// The platform bus consumer for one role: manual offset commit, cooperative
/// sticky assignment, static membership, and one record processed at a time.
///
/// One record at a time is a decision, not a simplification. A batch inside a
/// single transaction would hold the audit chain lock of a partition for the
/// whole batch, serializing against every concurrent ingestion; one invalid
/// record would take the valid ones down with it; and the bulk insert it would
/// justify is exactly what forbids reusing the ingestion use case that already
/// owns every rule. Offsets are still committed per poll batch, so the commit
/// cost stays amortized while at-least-once rests on the deduplication mark.
///
/// A transient failure never advances the offset: the consumer retries in
/// process and then stops reading the partition, which is how a log applies
/// backpressure. Nothing here reaches a dead-letter topic; only the processor
/// decides that a record is permanently unprocessable.
/// </summary>
internal sealed class KafkaConsumerService<TProcessor> : BackgroundService
    where TProcessor : IKafkaMessageProcessor
{
    private readonly KafkaConsumerPlan<TProcessor> _plan;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IKafkaConsumerGate _gate;
    private readonly KafkaConsumerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KafkaConsumerService<TProcessor>> _logger;
    private readonly Dictionary<TopicPartition, DateTimeOffset> _pausedUntil = [];

    public KafkaConsumerService(
        KafkaConsumerPlan<TProcessor> plan,
        IServiceScopeFactory scopeFactory,
        IKafkaConsumerGate gate,
        IOptions<KafkaConsumerOptions> options,
        TimeProvider timeProvider,
        ILogger<KafkaConsumerService<TProcessor>> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _plan = plan;
        _scopeFactory = scopeFactory;
        _gate = gate;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
        => Task.Run(() => RunAsync(stoppingToken), stoppingToken);

    private async Task RunAsync(CancellationToken stoppingToken)
    {
        try
        {
            await WaitForGateAsync(stoppingToken);
            using IConsumer<string, byte[]> consumer =
                new ConsumerBuilder<string, byte[]>(BuildConfig()).Build();
            consumer.Subscribe(_plan.Topics);
            var topics = string.Join(",", _plan.Topics);
            _logger.KafkaConsumerStarted(topics, _plan.GroupId);
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await RunPassAsync(consumer, stoppingToken);
                }
            }
            finally
            {
                // Leaves the group cleanly so the partitions move without
                // waiting for the session timeout.
                consumer.Close();
            }
        }
        catch (OperationCanceledException)
        {
            // Host shutdown: uncommitted offsets replay after the rebalance.
        }
    }

    /// <summary>
    /// Blocks the subscription until the role's precondition holds. A role
    /// that cannot decide yet must not read records it would refuse for the
    /// wrong reason and lose to the topic retention.
    /// </summary>
    private async Task WaitForGateAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            KafkaGateDecision decision = await _gate.EvaluateAsync(stoppingToken);
            if (decision.CanConsume)
            {
                return;
            }

            _logger.KafkaConsumerGateClosed(_plan.GroupId, decision.Reason ?? string.Empty);
            await Task.Delay(TimeSpan.FromSeconds(_options.PauseSeconds), stoppingToken);
        }

        stoppingToken.ThrowIfCancellationRequested();
    }

    private async Task<KafkaConsumePassResult> RunPassAsync(
        IConsumer<string, byte[]> consumer,
        CancellationToken stoppingToken)
    {
        ResumeExpiredPauses(consumer);

        var received = 0;
        var processed = 0;
        var duplicates = 0;
        var deadLettered = 0;
        var paused = 0;
        var stored = false;

        for (var index = 0; index < _options.BatchSize && !stoppingToken.IsCancellationRequested; index++)
        {
            ConsumeResult<string, byte[]>? result;
            try
            {
                result = consumer.Consume(TimeSpan.FromMilliseconds(_options.PollTimeoutMilliseconds));
            }
            catch (ConsumeException exception)
            {
                _logger.KafkaConsumePassFailed(_plan.GroupId, exception);
                break;
            }

            if (result is null || result.IsPartitionEOF)
            {
                break;
            }

            received++;
            KafkaDisposition disposition = await SettleAsync(result, stoppingToken);
            if (disposition is KafkaDisposition.Retry retry)
            {
                PausePartition(consumer, result.TopicPartition, retry.Reason);
                paused++;
                break;
            }

            switch (disposition)
            {
                case KafkaDisposition.Processed:
                    processed++;
                    break;
                case KafkaDisposition.Duplicate:
                    duplicates++;
                    break;
                case KafkaDisposition.DeadLetter deadLetter:
                    deadLettered++;
                    _logger.KafkaRecordDeadLettered(
                        result.Topic, result.Partition.Value, result.Offset.Value, deadLetter.Reason);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Disposição de mensagem não suportada: {disposition.GetType().Name}.");
            }

            consumer.StoreOffset(result);
            stored = true;
        }

        if (stored)
        {
            // One commit per poll batch: the deduplication mark, not the
            // offset, is what makes a redelivery harmless.
            consumer.Commit();
        }

        return new KafkaConsumePassResult(received, processed, duplicates, deadLettered, paused);
    }

    /// <summary>
    /// Runs the processor with bounded in-process retries. A failure that
    /// survives them is reported as a retry disposition, which stops the
    /// partition instead of advancing past a record nothing committed.
    /// </summary>
    private async Task<KafkaDisposition> SettleAsync(
        ConsumeResult<string, byte[]> result,
        CancellationToken stoppingToken)
    {
        KafkaMessageContext context = BuildContext(result);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using IServiceScope scope = _scopeFactory.CreateScope();
                TProcessor processor = scope.ServiceProvider.GetRequiredService<TProcessor>();
                return await processor.ProcessAsync(context, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (attempt >= _options.TransientRetryAttempts)
                {
                    _logger.KafkaRecordFailedTransiently(
                        context.Topic, context.Partition, context.Offset, attempt, exception);
                    return new KafkaDisposition.Retry(exception.GetType().Name);
                }

                await Task.Delay(BackoffFor(attempt), stoppingToken);
            }
        }
    }

    private TimeSpan BackoffFor(int attempt)
        => TimeSpan.FromMilliseconds(_options.TransientRetryBaseMilliseconds * Math.Pow(2, attempt));

    private KafkaMessageContext BuildContext(ConsumeResult<string, byte[]> result)
    {
        var body = Encoding.UTF8.GetString(result.Message.Value ?? []);
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (IHeader header in result.Message.Headers ?? [])
        {
            headers[header.Key] = Encoding.UTF8.GetString(header.GetValueBytes() ?? []);
        }

        var context = new KafkaMessageContext
        {
            Topic = result.Topic,
            Partition = result.Partition.Value,
            Offset = result.Offset.Value,
            Key = result.Message.Key,
            Headers = headers,
            Body = body,
        };

        if ((result.Message.Value?.Length ?? 0) > _options.MaxBodyBytes)
        {
            return context with { InvalidReason = KafkaConsumerReasons.BodyTooLarge };
        }

        CloudEventParse parse = CloudEventParser.Parse(body);
        return parse.Event is { } cloudEvent
            ? context with { Event = cloudEvent }
            : context with { InvalidReason = parse.InvalidReason };
    }

    private void PausePartition(
        IConsumer<string, byte[]> consumer,
        TopicPartition partition,
        string reason)
    {
        consumer.Pause([partition]);
        _pausedUntil[partition] = _timeProvider.GetUtcNow().AddSeconds(_options.PauseSeconds);
        _logger.KafkaPartitionPaused(partition.Topic, partition.Partition.Value, reason, _options.PauseSeconds);
    }

    private void ResumeExpiredPauses(IConsumer<string, byte[]> consumer)
    {
        if (_pausedUntil.Count == 0)
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        TopicPartition[] due = [.. _pausedUntil.Where(entry => entry.Value <= now).Select(entry => entry.Key)];
        if (due.Length == 0)
        {
            return;
        }

        consumer.Resume(due);
        foreach (TopicPartition partition in due)
        {
            _pausedUntil.Remove(partition);
            _logger.KafkaPartitionResumed(partition.Topic, partition.Partition.Value);
        }
    }

    private ConsumerConfig BuildConfig()
    {
        var config = new ConsumerConfig
        {
            BootstrapServers = _options.BootstrapServers,
            GroupId = _plan.GroupId,
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,
            EnablePartitionEof = false,
            PartitionAssignmentStrategy = PartitionAssignmentStrategy.CooperativeSticky,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            MaxPollIntervalMs = _options.MaxPollIntervalMilliseconds,
            SessionTimeoutMs = _options.SessionTimeoutMilliseconds,
        };

        if (_options.GroupInstanceId is { Length: > 0 } instanceId)
        {
            config.GroupInstanceId = instanceId;
        }

        if (Enum.TryParse(_options.SecurityProtocol, ignoreCase: true, out SecurityProtocol protocol))
        {
            config.SecurityProtocol = protocol;
        }

        if (_options.SaslMechanism is { Length: > 0 } mechanismName
            && Enum.TryParse(mechanismName, ignoreCase: true, out SaslMechanism mechanism))
        {
            config.SaslMechanism = mechanism;
            config.SaslUsername = _options.SaslUsername;
            config.SaslPassword = _options.SaslPassword;
        }

        return config;
    }
}

/// <summary>Stable technical reasons the platform consumer reports for an unreadable record.</summary>
public static class KafkaConsumerReasons
{
    public const string BodyTooLarge = "record-body-too-large";
}
