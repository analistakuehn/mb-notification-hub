namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;

internal static partial class ProviderRateLimiterLogger
{
    [LoggerMessage(EventId = 6020, Level = LogLevel.Information, Message = "Envio barrado pelo limite de taxa do provedor {ProviderKey} ({PermitsPerSecond}/s); a mensagem volta à fila sem chamada ao provedor.")]
    internal static partial void ProviderRateLimitReached(this ILogger logger, string providerKey, int permitsPerSecond);

    [LoggerMessage(EventId = 6021, Level = LogLevel.Error, Message = "Alarme: o Redis do limite de taxa por provedor está indisponível; o envio seguiu sem limite (fail-open). Compensação manual: kill switch de canal.")]
    internal static partial void ProviderRateLimitStoreUnavailable(this ILogger logger, Exception exception);
}
