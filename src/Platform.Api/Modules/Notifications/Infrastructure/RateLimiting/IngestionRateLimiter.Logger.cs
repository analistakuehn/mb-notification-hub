namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

internal static partial class IngestionRateLimiterLogger
{
    [LoggerMessage(EventId = 7010, Level = LogLevel.Error, Message = "Alarme: o Redis do rate limit de ingestão está indisponível; a requisição seguiu sem limite (fail-open). Compensação manual: kill switch.")]
    internal static partial void RateLimitStoreUnavailable(this ILogger logger, Exception exception);
}
