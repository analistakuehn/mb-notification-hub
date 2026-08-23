using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;

/// <summary>
/// Wire shape of one Mail Send v3 call. The rendered HTML travels as-is:
/// this hub owns and audits its templates, so provider-side dynamic
/// templates are never used.
/// </summary>
internal sealed record SendGridMailRequest(
    [property: JsonPropertyName("personalizations")] IReadOnlyList<SendGridPersonalization> Personalizations,
    [property: JsonPropertyName("from")] SendGridAddress From,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("content")] IReadOnlyList<SendGridContent> Content,
    [property: JsonPropertyName("mail_settings")] SendGridMailSettings MailSettings);

internal sealed record SendGridPersonalization(
    [property: JsonPropertyName("to")] IReadOnlyList<SendGridAddress> To);

internal sealed record SendGridAddress(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("name")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Name);

internal sealed record SendGridContent(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("value")] string Value);

internal sealed record SendGridMailSettings(
    [property: JsonPropertyName("sandbox_mode")] SendGridSandboxMode SandboxMode);

internal sealed record SendGridSandboxMode(
    [property: JsonPropertyName("enable")] bool Enable);

internal sealed record SendGridErrorResponse(
    [property: JsonPropertyName("errors")] IReadOnlyList<SendGridError>? Errors);

internal sealed record SendGridError(
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("field")] string? Field);
