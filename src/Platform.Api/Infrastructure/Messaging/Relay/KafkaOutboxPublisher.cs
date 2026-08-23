using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

/// <summary>
/// Publishes claimed rows to the corporate bus with an idempotent producer.
/// The record value is the stored payload text exactly as the database
/// returned it, never re-serialized, so the envelope the producing module
/// wrote is byte for byte the envelope a consumer reads; the key is
/// <c>message_key</c> and the stored headers travel as record headers next to
/// <c>eventType</c>.
///
/// Durability is not negotiable here: <c>acks=all</c> plus
/// <c>enable.idempotence=true</c> is what makes an accepted delivery report a
/// real acceptance, which is the only condition under which the relay may
/// stamp <c>sent_at</c>. Reports are awaited per message inside windows
/// bounded by the configured concurrency, so one refused record never turns
/// the whole batch pending.
/// </summary>
internal sealed class KafkaOutboxPublisher : IOutboxPublisher, IDisposable
{
    private const string EventTypeHeader = "eventType";

    private readonly IProducer<string, byte[]> _producer;
    private readonly IOptions<OutboxRelayOptions> _relayOptions;
    private readonly OutboxKafkaOptions _kafkaOptions;
    private readonly OutboxRelayHealthState _healthState;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<KafkaOutboxPublisher> _logger;

    public KafkaOutboxPublisher(
        IOptions<OutboxKafkaOptions> kafkaOptions,
        IOptions<OutboxRelayOptions> relayOptions,
        OutboxRelayHealthState healthState,
        TimeProvider timeProvider,
        ILogger<KafkaOutboxPublisher> logger)
    {
        ArgumentNullException.ThrowIfNull(kafkaOptions);
        _kafkaOptions = kafkaOptions.Value;
        _relayOptions = relayOptions;
        _healthState = healthState;
        _timeProvider = timeProvider;
        _logger = logger;
        _producer = new ProducerBuilder<string, byte[]>(BuildConfig(_kafkaOptions)).Build();
    }

    public async Task<OutboxPublishOutcome> PublishAsync(
        IReadOnlyList<PendingOutboxMessage> messages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var accepted = new List<Guid>();
        var failures = new List<OutboxPublishFailure>();

        foreach (PendingOutboxMessage[] window in messages.Chunk(_relayOptions.Value.PublishConcurrency))
        {
            var inFlight = new List<(PendingOutboxMessage Message, Task<DeliveryResult<string, byte[]>>? Report)>(
                window.Length);
            foreach (PendingOutboxMessage message in window)
            {
                inFlight.Add((message, StartProduce(message, failures, cancellationToken)));
            }

            foreach ((PendingOutboxMessage message, Task<DeliveryResult<string, byte[]>>? report) in inFlight)
            {
                if (report is null)
                {
                    continue;
                }

                await AwaitReportAsync(message, report, accepted, failures);
            }
        }

        return new OutboxPublishOutcome { AcceptedIds = accepted, Failures = failures };
    }

    public void Dispose()
    {
        // The flush drains outstanding reports so a graceful shutdown does not
        // abandon records the relay already claimed.
        try
        {
            _producer.Flush(TimeSpan.FromMilliseconds(_kafkaOptions.FlushTimeoutMilliseconds));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.KafkaFlushFailed(exception);
        }

        _producer.Dispose();
    }

    private static ProducerConfig BuildConfig(OutboxKafkaOptions options)
    {
        var config = new ProducerConfig
        {
            BootstrapServers = options.BootstrapServers,
            ClientId = options.ClientId,
            Acks = Acks.All,
            EnableIdempotence = true,
            // Five is the ceiling the idempotent producer keeps ordering under.
            MaxInFlight = 5,
            LingerMs = options.LingerMilliseconds,
            BatchSize = options.BatchSizeBytes,
            MessageTimeoutMs = options.DeliveryTimeoutMilliseconds,
        };

        if (Enum.TryParse(options.SecurityProtocol, ignoreCase: true, out SecurityProtocol protocol))
        {
            config.SecurityProtocol = protocol;
        }

        if (options.SaslMechanism is { Length: > 0 } mechanismName
            && Enum.TryParse(mechanismName, ignoreCase: true, out SaslMechanism mechanism))
        {
            config.SaslMechanism = mechanism;
            config.SaslUsername = options.SaslUsername;
            config.SaslPassword = options.SaslPassword;
        }

        return config;
    }

    private Task<DeliveryResult<string, byte[]>>? StartProduce(
        PendingOutboxMessage message,
        List<OutboxPublishFailure> failures,
        CancellationToken cancellationToken)
    {
        try
        {
            return _producer.ProduceAsync(message.Destination, BuildRecord(message), cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A synchronous refusal (full local queue, invalid record) leaves
            // the row pending exactly like a refused delivery report.
            _logger.KafkaProduceCallFailed(message.Destination, exception);
            failures.Add(new OutboxPublishFailure(
                message.Id, message.Destination, exception.GetType().Name));
            return null;
        }
    }

    private async Task AwaitReportAsync(
        PendingOutboxMessage message,
        Task<DeliveryResult<string, byte[]>> report,
        List<Guid> accepted,
        List<OutboxPublishFailure> failures)
    {
        try
        {
            await report;
            _healthState.ReportQueueAvailable(message.Destination);
            accepted.Add(message.Id);
        }
        catch (ProduceException<string, byte[]> exception)
        {
            if (exception.Error.Code is ErrorCode.UnknownTopicOrPart or ErrorCode.TopicException)
            {
                _healthState.ReportQueueMissing(message.Destination, _timeProvider.GetUtcNow());
                _logger.KafkaTopicMissing(message.Destination);
            }
            else
            {
                _logger.KafkaRecordRejected(message.Destination, exception.Error.Reason);
            }

            failures.Add(new OutboxPublishFailure(
                message.Id, message.Destination, exception.Error.Code.ToString()));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // At-least-once by construction: the record may still have reached
            // the broker and the row republishes on a later pass.
            _logger.KafkaProduceCallFailed(message.Destination, exception);
            failures.Add(new OutboxPublishFailure(
                message.Id, message.Destination, exception.GetType().Name));
        }
    }

    private static Message<string, byte[]> BuildRecord(PendingOutboxMessage message)
    {
        var headers = new Headers
        {
            { EventTypeHeader, Encoding.UTF8.GetBytes(message.EventType) },
        };

        using JsonDocument stored = JsonDocument.Parse(message.HeadersJson);
        if (stored.RootElement.ValueKind is JsonValueKind.Object)
        {
            foreach (JsonProperty header in stored.RootElement.EnumerateObject())
            {
                if (header.Value.ValueKind is JsonValueKind.String
                    && header.Value.GetString() is { Length: > 0 } value
                    && !string.Equals(header.Name, EventTypeHeader, StringComparison.Ordinal))
                {
                    headers.Add(header.Name, Encoding.UTF8.GetBytes(value));
                }
            }
        }

        return new Message<string, byte[]>
        {
            Key = message.MessageKey,
            // The stored jsonb payload text, byte for byte; the envelope was
            // written by the producer and the relay never re-wraps it.
            Value = Encoding.UTF8.GetBytes(message.PayloadJson),
            Headers = headers,
        };
    }
}
