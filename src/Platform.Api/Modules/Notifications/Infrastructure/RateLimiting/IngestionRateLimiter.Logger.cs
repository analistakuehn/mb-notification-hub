namespace NotificationHub.Api.Modules.Notifications.Infrastructure.RateLimiting;

internal static partial class IngestionRateLimiterLogger
{
    [LoggerMessage(EventId = 7010, Level = LogLevel.Error, Message = "Alarme: o Redis do rate limit de ingestão está indisponível; a requisição seguiu sem limite (fail-open). Compensação manual: kill switch.")]
    internal static partial void RateLimitStoreUnavailable(this ILogger logger, Exception exception);

    [LoggerMessage(EventId = 7011, Level = LogLevel.Warning, Message = "Alarme: o principal {Principal} ultrapassou o limite da classe {Class} ({Count} contra {PermitLimit}) na entrada pelo barramento; observado sem rejeitar. Parada real: kill switch e ACL do broker.")]
    internal static partial void PrincipalLimitObserved(this ILogger logger, string principal, string @class, long count, int permitLimit);
}
