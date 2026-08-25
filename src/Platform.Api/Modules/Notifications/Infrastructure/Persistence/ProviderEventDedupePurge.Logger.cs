namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

internal static partial class ProviderEventDedupePurgeLogger
{
    [LoggerMessage(
        EventId = 7050,
        Level = LogLevel.Information,
        Message = "Removidas {Removed} marcas de deduplicação de provedor anteriores a {Threshold}.")]
    internal static partial void ProviderEventDedupePurged(
        this ILogger logger, int removed, DateTimeOffset threshold);

    [LoggerMessage(
        EventId = 7051,
        Level = LogLevel.Information,
        Message = "Purga de deduplicação de provedor desabilitada por configuração.")]
    internal static partial void ProviderEventDedupePurgeDisabled(this ILogger logger);

    [LoggerMessage(
        EventId = 7052,
        Level = LogLevel.Information,
        Message = "Purga de deduplicação de provedor iniciada com intervalo {Interval} "
            + "e retenção {Retention}.")]
    internal static partial void ProviderEventDedupePurgeStarted(
        this ILogger logger, TimeSpan interval, TimeSpan retention);

    [LoggerMessage(
        EventId = 7053,
        Level = LogLevel.Error,
        Message = "Falha em uma rodada da purga de deduplicação de provedor; "
            + "nova tentativa no próximo ciclo.")]
    internal static partial void ProviderEventDedupePurgeRoundFailed(
        this ILogger logger, Exception exception);
}
