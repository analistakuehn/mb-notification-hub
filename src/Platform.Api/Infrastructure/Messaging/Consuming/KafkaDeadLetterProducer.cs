using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

/// <summary>
/// Idempotent producer dedicated to the dead-letter topics of the consuming
/// roles. It shares the durability posture of the relay producer, because a
/// dead-letter record that was never acknowledged is a message the hub
/// silently dropped.
/// </summary>
internal sealed class KafkaDeadLetterProducer : IKafkaDeadLetterProducer, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;

    public KafkaDeadLetterProducer(IOptions<KafkaConsumerOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        KafkaConsumerOptions value = options.Value;
        var config = new ProducerConfig
        {
            BootstrapServers = value.BootstrapServers,
            ClientId = "notification-hub-dead-letter",
            Acks = Acks.All,
            EnableIdempotence = true,
            MaxInFlight = 5,
        };

        if (Enum.TryParse(value.SecurityProtocol, ignoreCase: true, out SecurityProtocol protocol))
        {
            config.SecurityProtocol = protocol;
        }

        if (value.SaslMechanism is { Length: > 0 } mechanismName
            && Enum.TryParse(mechanismName, ignoreCase: true, out SaslMechanism mechanism))
        {
            config.SaslMechanism = mechanism;
            config.SaslUsername = value.SaslUsername;
            config.SaslPassword = value.SaslPassword;
        }

        _producer = new ProducerBuilder<string, byte[]>(config).Build();
    }

    public async Task ProduceAsync(DeadLetterRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);

        var headers = new Headers();
        foreach ((var name, var value) in record.Headers)
        {
            headers.Add(name, Encoding.UTF8.GetBytes(value));
        }

        await _producer.ProduceAsync(
            record.Topic,
            new Message<string, byte[]>
            {
                Key = record.Key ?? string.Empty,
                Value = Encoding.UTF8.GetBytes(record.Body),
                Headers = headers,
            },
            cancellationToken);
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(10));
        _producer.Dispose();
    }
}
