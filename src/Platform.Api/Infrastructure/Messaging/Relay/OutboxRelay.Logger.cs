namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

internal static partial class OutboxRelayLogger
{
    [LoggerMessage(EventId = 5110, Level = LogLevel.Warning, Message = "Mensagens do outbox permanecem pendentes para o destino {Destination}: {PendingCount}; idade do pendente mais antigo: {OldestPendingSeconds}s; primeiro motivo: {FirstReason}.")]
    internal static partial void MessagesLeftPending(this ILogger logger, string destination, int pendingCount, double oldestPendingSeconds, string firstReason);
}
