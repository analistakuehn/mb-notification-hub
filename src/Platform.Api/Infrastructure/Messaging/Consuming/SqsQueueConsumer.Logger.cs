namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

internal static partial class SqsQueueConsumerLogger
{
    [LoggerMessage(EventId = 7300, Level = LogLevel.Warning, Message = "A fila {QueueName} não existe; o consumidor aguarda o provisionamento pela infraestrutura.")]
    internal static partial void ConsumerQueueMissing(this ILogger logger, string queueName);

    [LoggerMessage(EventId = 7301, Level = LogLevel.Information, Message = "Reentrega detectada na fila {QueueName} para a mensagem {EnvelopeMessageId} do tipo {EventType}; nenhum efeito repetido.")]
    internal static partial void ConsumerDuplicateDetected(this ILogger logger, string queueName, string eventType, Guid envelopeMessageId);

    [LoggerMessage(EventId = 7302, Level = LogLevel.Warning, Message = "Mensagem {SqsMessageId} descartada da fila {QueueName} por erro permanente: {Reason}.")]
    internal static partial void ConsumerMessageDiscarded(this ILogger logger, string queueName, string reason, string sqsMessageId);

    [LoggerMessage(EventId = 7303, Level = LogLevel.Warning, Message = "Falha transitória ao processar a mensagem {SqsMessageId} da fila {QueueName} na entrega {ReceiveCount}; visibilidade estendida em {DelaySeconds} s.")]
    internal static partial void ConsumerMessageFailedTransiently(this ILogger logger, string queueName, string sqsMessageId, int receiveCount, int delaySeconds, Exception exception);

    [LoggerMessage(EventId = 7304, Level = LogLevel.Warning, Message = "Não foi possível estender a visibilidade da mensagem {SqsMessageId} da fila {QueueName} em {Instant}; o timeout original devolve a mensagem.")]
    internal static partial void ConsumerVisibilityChangeFailed(this ILogger logger, string queueName, string sqsMessageId, DateTimeOffset instant, Exception exception);

    [LoggerMessage(EventId = 7305, Level = LogLevel.Information, Message = "Consumidor SQS iniciado para as filas {QueueNames} com {Concurrency} vagas de processamento.")]
    internal static partial void ConsumerServiceStarted(this ILogger logger, string queueNames, int concurrency);

    [LoggerMessage(EventId = 7306, Level = LogLevel.Error, Message = "Falha na passada de consumo da fila {QueueName}; nova tentativa após o intervalo.")]
    internal static partial void ConsumerPassFailed(this ILogger logger, string queueName, Exception exception);

    [LoggerMessage(EventId = 7307, Level = LogLevel.Information, Message = "Mensagem {SqsMessageId} devolvida à fila {QueueName} por decisão do processador ({Reason}); retorno em {DelaySeconds} s.")]
    internal static partial void ConsumerMessagePostponed(this ILogger logger, string queueName, string sqsMessageId, string reason, int delaySeconds);
}
