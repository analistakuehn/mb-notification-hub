using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

/// <summary>Answer of the OAuth JWT-bearer grant.</summary>
internal sealed record FcmTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("expires_in")] long ExpiresInSeconds);
