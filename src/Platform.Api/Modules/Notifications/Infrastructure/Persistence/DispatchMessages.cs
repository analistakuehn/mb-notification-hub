using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Builders of the internal queue messages the dispatch path produces, all in
/// the ratified claim-check envelope: identifiers only, never content. One
/// builder per message type, shared by every producer inside this module so
/// the wire shape can never drift between the pipeline commit, the fan-out
/// expansion and the fallback trigger.
/// </summary>
internal static class DispatchMessages
{
    internal const string AttemptQueuedType = "attempt.queued";
    internal const string FallbackRequestedType = "fallback.requested";
    internal const int SchemaVersion = 1;

    /// <summary>One queued attempt announced to its dispatch queue.</summary>
    internal static OutboxAppend BuildAttemptQueued(
        string destination,
        string recipientId,
        string priorityClass,
        Guid notificationId,
        Guid attemptId,
        DateTimeOffset occurredAt,
        string? traceparent)
        => Build(
            destination,
            AttemptQueuedType,
            recipientId,
            priorityClass,
            occurredAt,
            traceparent,
            new { notificationId, attemptId });

    /// <summary>
    /// One definitive failure asking the Core for the next plan step, routed
    /// to the core queue of the notification's class.
    /// </summary>
    internal static OutboxAppend BuildFallbackRequested(
        string recipientId,
        string priorityClass,
        Guid notificationId,
        Guid failedAttemptId,
        DateTimeOffset occurredAt,
        string? traceparent)
        => Build(
            $"core-{priorityClass}",
            FallbackRequestedType,
            recipientId,
            priorityClass,
            occurredAt,
            traceparent,
            new { notificationId, failedAttemptId });

    private static OutboxAppend Build(
        string destination,
        string type,
        string recipientId,
        string priorityClass,
        DateTimeOffset occurredAt,
        string? traceparent,
        object payload)
        => new()
        {
            Destination = destination,
            EventType = type,
            MessageKey = recipientId,
            HeadersJson = traceparent is null
                ? "{}"
                : JsonSerializer.Serialize(new { traceparent }),
            PayloadJson = JsonSerializer.Serialize(new
            {
                messageId = Guid.CreateVersion7(),
                type,
                schemaVersion = SchemaVersion,
                occurredAt,
                traceparent,
                priorityClass,
                payload,
            }),
            PriorityClass = priorityClass,
        };
}
