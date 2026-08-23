using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

/// <summary>Wire shape of one HTTP v1 <c>messages:send</c> call.</summary>
internal sealed record FcmSendRequest(
    [property: JsonPropertyName("message")] FcmMessage Message);

internal sealed record FcmMessage(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("notification")] FcmNotification Notification,
    [property: JsonPropertyName("data")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, string>? Data);

internal sealed record FcmNotification(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body);

internal sealed record FcmSendResponse(
    [property: JsonPropertyName("name")] string? Name);

internal sealed record FcmErrorResponse(
    [property: JsonPropertyName("error")] FcmErrorBody? Error);

internal sealed record FcmErrorBody(
    [property: JsonPropertyName("code")] int Code,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("details")] IReadOnlyList<FcmErrorDetail>? Details);

internal sealed record FcmErrorDetail(
    [property: JsonPropertyName("@type")] string? Type,
    [property: JsonPropertyName("errorCode")] string? ErrorCode);
