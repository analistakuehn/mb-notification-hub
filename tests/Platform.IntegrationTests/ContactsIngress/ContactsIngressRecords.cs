using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.ContactConsent.Features.Ingress;

namespace NotificationHub.IntegrationTests.ContactsIngress;

/// <summary>
/// Drives the ingestion processor over records that really exist on the
/// broker. The record is produced first, so its topic, partition and offset
/// are the broker's own, and the dead-letter production the processor performs
/// lands on the real topic the assertions read back.
/// </summary>
internal static class ContactsIngressRecords
{
    internal const string ContactPointsDeclaredType = "araia.contact.contact_points_declared.v1";
    internal const string ConsentsDeclaredType = "araia.contact.consents_declared.v1";

    internal static string NewRecipientId() => $"cus_{Guid.NewGuid():N}";

    /// <summary>Builds the message context the platform consumer would hand the processor.</summary>
    internal static KafkaMessageContext Context(
        TopicPartitionOffset position,
        string key,
        string body)
    {
        var context = new KafkaMessageContext
        {
            Topic = position.Topic,
            Partition = position.Partition.Value,
            Offset = position.Offset.Value,
            Key = key,
            Headers = new Dictionary<string, string>(StringComparer.Ordinal),
            Body = body,
        };

        CloudEventParse parse = CloudEventParser.Parse(body);
        return parse.Event is { } cloudEvent
            ? context with { Event = cloudEvent }
            : context with { InvalidReason = parse.InvalidReason };
    }

    /// <summary>Produces the record and settles it through the ingestion processor.</summary>
    internal static async Task<KafkaDisposition> ProcessAsync(
        ContactsIngressFixture fixture,
        ServiceProvider provider,
        string key,
        string body)
    {
        TopicPartitionOffset position = await fixture.ProduceAsync(
            ContactsIngressFixture.ContactsTopic, key, body);
        return await SettleAsync(provider, Context(position, key, body));
    }

    internal static async Task<KafkaDisposition> SettleAsync(
        ServiceProvider provider,
        KafkaMessageContext context)
    {
        using IServiceScope scope = provider.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<ContactsIngressProcessor>()
            .ProcessAsync(context, CancellationToken.None);
    }

    /// <summary>One well-formed declaration of the complete contact-point set.</summary>
    internal static string ContactPointsDeclaredEvent(
        string recipientId,
        object[] contactPoints,
        string source = ContactsIngressFixture.AcceptedSource,
        string? timezone = null,
        string? eventId = null)
        => Envelope(
            ContactPointsDeclaredType,
            recipientId,
            source,
            eventId,
            new { timezone, contactPoints });

    /// <summary>One well-formed declaration of the desired consent state.</summary>
    internal static string ConsentsDeclaredEvent(
        string recipientId,
        object[] consents,
        string source = ContactsIngressFixture.AcceptedSource,
        string? eventId = null)
        => Envelope(ConsentsDeclaredType, recipientId, source, eventId, new { consents });

    internal static string Envelope(
        string type,
        string recipientId,
        string source,
        string? eventId,
        object data)
        => JsonSerializer.Serialize(new
        {
            specversion = "1.0",
            id = eventId ?? $"evt-{Guid.NewGuid():N}",
            source,
            type,
            time = DateTimeOffset.UtcNow,
            subject = recipientId,
            datacontenttype = "application/json",
            data,
        });

    internal static object ContactPoint(string channel, string value, bool verified = true)
        => new { channel, value, verified };

    internal static object Consent(
        string purpose,
        string channel,
        bool granted,
        string source = "app",
        string termsVersion = "v1")
        => new { purpose, channel, granted, source, termsVersion };

    /// <summary>Header value of one dead-letter record, or null when the header is absent.</summary>
    internal static string? Header(ConsumeResult<string, byte[]> record, string name)
    {
        IHeader? header = record.Message.Headers?.FirstOrDefault(
            candidate => string.Equals(candidate.Key, name, StringComparison.Ordinal));
        return header is null ? null : Encoding.UTF8.GetString(header.GetValueBytes());
    }

    internal static string Body(ConsumeResult<string, byte[]> record)
        => Encoding.UTF8.GetString(record.Message.Value ?? []);

    /// <summary>
    /// The dead-letter record of one recipient, read back from the real topic.
    /// The read retries because the produce is already confirmed by the time
    /// the caller asks: an empty read is a reader that has not joined the
    /// group yet, never a record that does not exist.
    /// </summary>
    internal static ConsumeResult<string, byte[]> DeadLetterOf(
        ContactsIngressFixture fixture,
        string recipientId)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        do
        {
            ConsumeResult<string, byte[]>? match = fixture
                .ReadAll(ContactsIngressFixture.DeadLetterTopic, TimeSpan.FromSeconds(20))
                .LastOrDefault(record => record.Message.Key == recipientId);
            if (match is not null)
            {
                return match;
            }
        }
        while (DateTimeOffset.UtcNow < deadline);

        throw new InvalidOperationException(
            $"Nenhum registro de dead-letter para o destinatário {recipientId} dentro do orçamento de leitura.");
    }
}
