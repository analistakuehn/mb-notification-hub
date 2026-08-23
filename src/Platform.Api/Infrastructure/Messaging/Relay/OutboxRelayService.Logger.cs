namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

internal static partial class OutboxRelayServiceLogger
{
    [LoggerMessage(EventId = 5120, Level = LogLevel.Information, Message = "Relay do outbox iniciado; intervalo ocioso {PollInterval}, lote {BatchSize}, bandas {Bands}.")]
    internal static partial void OutboxRelayStarted(this ILogger logger, TimeSpan pollInterval, int batchSize, string bands);

    [LoggerMessage(EventId = 5121, Level = LogLevel.Error, Message = "Falha na passada do relay do outbox; nova tentativa no próximo intervalo. Nenhuma linha foi perdida: pendentes permanecem no outbox.")]
    internal static partial void OutboxRelayPassFailed(this ILogger logger, Exception exception);
}
