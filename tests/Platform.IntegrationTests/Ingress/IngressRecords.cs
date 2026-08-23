using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.Ingress;

namespace NotificationHub.IntegrationTests.Ingress;

/// <summary>
/// Drives the ingress processor over records that really exist on the broker.
/// The record is produced first, so its topic, partition and offset are the
/// broker's own, and the dead-letter production the processor performs lands
/// on the real topic the assertions read back.
/// </summary>
internal static class IngressRecords
{
    /// <summary>Builds the message context the platform consumer would hand the processor.</summary>
    internal static KafkaMessageContext Context(
        TopicPartitionOffset position,
        string key,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var context = new KafkaMessageContext
        {
            Topic = position.Topic,
            Partition = position.Partition.Value,
            Offset = position.Offset.Value,
            Key = key,
            Headers = headers ?? new Dictionary<string, string>(StringComparer.Ordinal),
            Body = body,
        };

        CloudEventParse parse = CloudEventParser.Parse(body);
        return parse.Event is { } cloudEvent
            ? context with { Event = cloudEvent }
            : context with { InvalidReason = parse.InvalidReason };
    }

    /// <summary>Produces the record and settles it through the ingress processor.</summary>
    internal static async Task<KafkaDisposition> ProcessAsync(
        KafkaIngressFixture fixture,
        ServiceProvider provider,
        string key,
        string body,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        TopicPartitionOffset position = await fixture.ProduceAsync(
            KafkaIngressFixture.RequestedTopic, key, body, headers);
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<KafkaIngressProcessor>()
            .ProcessAsync(Context(position, key, body, headers), CancellationToken.None);
    }

    /// <summary>Header value of one dead-letter record, or null when the header is absent.</summary>
    internal static string? Header(ConsumeResult<string, byte[]> record, string name)
    {
        IHeader? header = record.Message.Headers?.FirstOrDefault(
            candidate => string.Equals(candidate.Key, name, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }

    internal static string Body(ConsumeResult<string, byte[]> record)
        => Encoding.UTF8.GetString(record.Message.Value ?? []);
}
