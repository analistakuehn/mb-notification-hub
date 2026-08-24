using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

internal sealed record TwilioMessageResponse(
    [property: JsonPropertyName("sid")] string? Sid,
    [property: JsonPropertyName("code")] int? Code,
    [property: JsonPropertyName("message")] string? Message);
