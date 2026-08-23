namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

internal static partial class OutboxRelayLogger
{
    [LoggerMessage(EventId = 5110, Level = LogLevel.Warning, Message = "Mensagens do outbox permanecem pendentes no transporte {Transport} para o destino {Destination}: {PendingCount}; idade do pendente mais antigo: {OldestPendingSeconds}s; primeiro motivo: {FirstReason}.")]
    internal static partial void MessagesLeftPending(this ILogger logger, string transport, string destination, int pendingCount, double oldestPendingSeconds, string firstReason);
}
