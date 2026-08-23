using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;

namespace NotificationHub.IntegrationTests.Messaging;

/// <summary>Builds outbox rows shaped like the producing modules write them.</summary>
public static class OutboxEnvelopes
{
    public const string EventType = "notification.accepted";
    public const string Traceparent = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01";

    public static OutboxAppend Envelope(string destination, string priorityClass)
    {
        var traceparent = Traceparent;
        return new OutboxAppend
        {
            Destination = destination,
            EventType = EventType,
            MessageKey = $"cus_{Guid.NewGuid():N}",
            HeadersJson = JsonSerializer.Serialize(new { traceparent }),
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = Guid.CreateVersion7(),
                type = EventType,
                schemaVersion = 1,
                occurredAt = DateTimeOffset.UtcNow,
                traceparent,
                priorityClass,
                payload = new { notificationId = Guid.CreateVersion7() },
            }),
            PriorityClass = priorityClass,
        };
    }
}
