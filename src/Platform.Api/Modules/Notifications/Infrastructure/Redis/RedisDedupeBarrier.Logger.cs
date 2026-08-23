namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;

internal static partial class RedisDedupeBarrierLogger
{
    [LoggerMessage(EventId = 7100, Level = LogLevel.Error, Message = "Barreira de deduplicação indisponível no Redis; seguindo em fail-open com risco de duplicata aceito e auditado.")]
    internal static partial void DedupeBarrierUnavailable(this ILogger logger, Exception exception);
}
