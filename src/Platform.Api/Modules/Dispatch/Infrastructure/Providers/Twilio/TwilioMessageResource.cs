using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// One message resource as the provider reports it after the fact. Only the
/// fields the delivery answer is made of are read: the destination the
/// resource also carries is deliberately absent from this shape, so a payload
/// this module never asked for cannot reach a canonical event by accident.
/// </summary>
internal sealed record TwilioMessageResource(
    [property: JsonPropertyName("sid")] string? Sid,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("error_code")] int? ErrorCode,
    [property: JsonPropertyName("date_sent")] string? DateSent,
    [property: JsonPropertyName("date_updated")] string? DateUpdated);

/// <summary>One page of the message list, as the search route returns it.</summary>
internal sealed record TwilioMessagePage(
    [property: JsonPropertyName("messages")] IReadOnlyList<TwilioMessageResource>? Messages);
