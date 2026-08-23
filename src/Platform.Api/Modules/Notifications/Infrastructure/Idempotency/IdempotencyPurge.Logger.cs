namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;

internal static partial class IdempotencyPurgeLogger
{
    [LoggerMessage(EventId = 7030, Level = LogLevel.Information, Message = "Removidas {Removed} chaves de idempotência criadas antes de {Threshold}.")]
    internal static partial void IdempotencyRegistrationsPurged(this ILogger logger, int removed, DateTimeOffset threshold);

    [LoggerMessage(EventId = 7031, Level = LogLevel.Information, Message = "Purga de idempotência desabilitada por configuração.")]
    internal static partial void IdempotencyPurgeDisabled(this ILogger logger);

    [LoggerMessage(EventId = 7032, Level = LogLevel.Information, Message = "Purga de idempotência iniciada com intervalo {Interval} e retenção {Retention}.")]
    internal static partial void IdempotencyPurgeStarted(this ILogger logger, TimeSpan interval, TimeSpan retention);

    [LoggerMessage(EventId = 7033, Level = LogLevel.Error, Message = "Falha em uma rodada da purga de idempotência; nova tentativa no próximo ciclo.")]
    internal static partial void IdempotencyPurgeRoundFailed(this ILogger logger, Exception exception);
}
