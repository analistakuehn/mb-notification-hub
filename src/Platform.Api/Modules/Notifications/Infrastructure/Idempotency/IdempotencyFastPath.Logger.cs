namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Idempotency;

internal static partial class IdempotencyFastPathLogger
{
    [LoggerMessage(EventId = 7020, Level = LogLevel.Error, Message = "Alarme: o Redis do fast path de idempotência está indisponível; a requisição seguiu para o banco (fail-open), onde a chave única é a autoridade.")]
    internal static partial void IdempotencyStoreUnavailable(this ILogger logger, Exception exception);
}
