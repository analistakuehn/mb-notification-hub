using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

internal static class KillSwitchResumeMessages
{
    internal static OutboxAppend Build(
        KillSwitchHold hold,
        Notification notification,
        DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(hold.PayloadJson);
        JsonElement payload = document.RootElement;
        if (hold.WorkKind == KillSwitchWorkKinds.Dispatch && hold.ExpiresAt <= now)
        {
            Guid attemptId = RequiredGuid(payload, "attemptId");
            return DispatchMessages.BuildFallbackRequested(
                notification.RecipientId,
                notification.Class,
                notification.AuthFlow,
                notification.Id,
                attemptId,
                now,
                traceparent: null);
        }

        var eventType = hold.WorkKind switch
        {
            KillSwitchWorkKinds.Core => "notification.accepted",
            KillSwitchWorkKinds.Fallback => DispatchMessages.FallbackRequestedType,
            KillSwitchWorkKinds.Dispatch => DispatchMessages.AttemptQueuedType,
            _ => throw new InvalidOperationException(
                $"Tipo de trabalho em hold desconhecido: '{hold.WorkKind}'."),
        };
        return new OutboxAppend
        {
            Destination = hold.Destination,
            EventType = eventType,
            MessageKey = notification.RecipientId,
            HeadersJson = "{}",
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = hold.Id,
                type = eventType,
                schemaVersion = 1,
                occurredAt = now,
                traceparent = (string?)null,
                priorityClass = notification.Class,
                payload = payload.Clone(),
            }),
            PriorityClass = notification.Class,
        };
    }

    private static Guid RequiredGuid(JsonElement payload, string name)
        => payload.TryGetProperty(name, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && Guid.TryParse(value.GetString(), out Guid parsed)
                ? parsed
                : throw new InvalidOperationException($"O hold não contém '{name}' válido.");
}
