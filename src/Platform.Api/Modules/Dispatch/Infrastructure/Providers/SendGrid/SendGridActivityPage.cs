using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// One entry of the message activity search. Only the fields the delivery
/// answer is made of are read: the destination the entry also carries is
/// deliberately absent from this shape, so a payload this module never asked
/// for cannot reach a canonical event by accident.
/// </summary>
internal sealed record SendGridActivityMessage(
    [property: JsonPropertyName("msg_id")] string? MessageId,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("last_event_time")] string? LastEventTime);

/// <summary>One page of the message activity search.</summary>
internal sealed record SendGridActivityPage(
    [property: JsonPropertyName("messages")] IReadOnlyList<SendGridActivityMessage>? Messages);
