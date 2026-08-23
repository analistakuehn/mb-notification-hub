namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;

internal static partial class FcmAccessTokenSourceLogger
{
    [LoggerMessage(EventId = 6210, Level = LogLevel.Debug, Message = "Token de acesso do FCM renovado; expira em {ExpiresInSeconds} segundos.")]
    internal static partial void FcmTokenRenewed(this ILogger logger, long expiresInSeconds);

    [LoggerMessage(EventId = 6211, Level = LogLevel.Warning, Message = "Endpoint de token OAuth recusou a renovação (HTTP {StatusCode}).")]
    internal static partial void FcmTokenEndpointRejected(this ILogger logger, int statusCode);

    [LoggerMessage(EventId = 6212, Level = LogLevel.Warning, Message = "Endpoint de token OAuth inacessível.")]
    internal static partial void FcmTokenEndpointUnreachable(this ILogger logger, Exception exception);
}
