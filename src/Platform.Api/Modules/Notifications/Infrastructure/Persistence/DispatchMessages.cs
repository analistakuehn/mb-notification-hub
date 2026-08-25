using System.Text.Json;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;

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
    /// One exhausted plan step asking the Core for the next one. Every
    /// producer of this trigger builds it here, whatever moved the step:
    /// a definitive provider verdict, a target the hub could not use, or an
    /// elapsed deadline.
    /// </summary>
    internal static OutboxAppend BuildFallbackRequested(
        string recipientId,
        string priorityClass,
        bool authFlow,
        Guid notificationId,
        Guid failedAttemptId,
        DateTimeOffset occurredAt,
        string? traceparent)
        => Build(
            FallbackDestination(priorityClass, authFlow),
            FallbackRequestedType,
            recipientId,
            priorityClass,
            occurredAt,
            traceparent,
            new { notificationId, failedAttemptId });

    /// <summary>
    /// One parked notification handed back to the Core after its release
    /// instant passed. The message is the same one the ingestion writes, and
    /// deliberately so: the pipeline resumes from the stage list, not from a
    /// resumption path of its own, so a release must be indistinguishable from
    /// a first acceptance by the time it reaches the consumer.
    /// </summary>
    internal static OutboxAppend BuildNotificationAccepted(
        string recipientId,
        string priorityClass,
        bool authFlow,
        Guid notificationId,
        DateTimeOffset occurredAt,
        string? traceparent)
        => Build(
            CoreDestination(priorityClass, authFlow),
            CoreMessageProcessor.AcceptedMessageType,
            recipientId,
            priorityClass,
            occurredAt,
            traceparent,
            new { notificationId });

    /// <summary>
    /// Core queue one fallback trigger goes to. The relay reads the band off
    /// the destination, so an authentication flow has to name the
    /// authentication queue here for its next step to keep the top band the
    /// dispatch side already gives it; naming the class queue instead would
    /// drain the second half of an authentication code behind ordinary
    /// critical traffic.
    /// </summary>
    internal static string FallbackDestination(string priorityClass, bool authFlow)
        => CoreDestination(priorityClass, authFlow);

    /// <summary>
    /// The single rule that picks a Core queue, shared by every producer of
    /// one. Naming it once is what keeps the drain band of a message from
    /// depending on which producer wrote it.
    /// <para>
    /// A released notification always comes out of here on the class queue,
    /// because the silence window guard refuses to defer a critical or an
    /// authentication flow at all, so the authentication branch is unreachable
    /// from the release path today. It is still read from the stored signal
    /// rather than assumed away: the day that guard is loosened, the band of a
    /// released authentication code has to follow the loosening on its own.
    /// </para>
    /// </summary>
    internal static string CoreDestination(string priorityClass, bool authFlow)
        => authFlow ? OutboxBands.AuthDestination : $"core-{priorityClass}";

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
