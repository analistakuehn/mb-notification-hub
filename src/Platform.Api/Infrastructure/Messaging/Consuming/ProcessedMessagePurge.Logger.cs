namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

internal static partial class ProcessedMessagePurgeLogger
{
    [LoggerMessage(EventId = 7310, Level = LogLevel.Information, Message = "Purga de processed_messages removeu {Removed} marcas anteriores a {Threshold}.")]
    internal static partial void ProcessedMessagesPurged(this ILogger logger, int removed, DateTimeOffset threshold);

    [LoggerMessage(EventId = 7311, Level = LogLevel.Information, Message = "Purga de processed_messages desabilitada por configuração.")]
    internal static partial void ProcessedMessagePurgeDisabled(this ILogger logger);

    [LoggerMessage(EventId = 7312, Level = LogLevel.Information, Message = "Purga de processed_messages iniciada com intervalo {Interval} e retenção {Retention}.")]
    internal static partial void ProcessedMessagePurgeStarted(this ILogger logger, TimeSpan interval, TimeSpan retention);

    [LoggerMessage(EventId = 7313, Level = LogLevel.Error, Message = "Falha na rodada de purga de processed_messages; nova tentativa no próximo intervalo.")]
    internal static partial void ProcessedMessagePurgeRoundFailed(this ILogger logger, Exception exception);
}
