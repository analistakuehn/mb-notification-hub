namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

internal static partial class SqsOutboxPublisherLogger
{
    [LoggerMessage(EventId = 5100, Level = LogLevel.Critical, Message = "Fila SQS inexistente para o destino {Destination}; {PendingCount} mensagens permanecem pendentes. O relay nunca cria filas: provisione a fila pela infraestrutura.")]
    internal static partial void QueueMissing(this ILogger logger, string destination, int pendingCount);

    [LoggerMessage(EventId = 5101, Level = LogLevel.Error, Message = "Falha ao resolver a URL da fila do destino {Destination}; {PendingCount} mensagens permanecem pendentes até a próxima passada.")]
    internal static partial void QueueResolutionFailed(this ILogger logger, string destination, int pendingCount, Exception exception);

    [LoggerMessage(EventId = 5102, Level = LogLevel.Warning, Message = "SQS rejeitou {RejectedCount} entradas do lote para o destino {Destination}; primeiro motivo: {FirstReason}. As linhas rejeitadas permanecem pendentes.")]
    internal static partial void BatchEntriesRejected(this ILogger logger, string destination, int rejectedCount, string firstReason);

    [LoggerMessage(EventId = 5103, Level = LogLevel.Error, Message = "Falha na chamada de publicação para o destino {Destination}; {ChunkSize} mensagens permanecem pendentes e serão reenviadas na próxima passada.")]
    internal static partial void PublishCallFailed(this ILogger logger, string destination, int chunkSize, Exception exception);
}
