namespace NotificationHub.Api.Infrastructure.Messaging.Relay;

internal static partial class KafkaOutboxPublisherLogger
{
    [LoggerMessage(EventId = 5140, Level = LogLevel.Error, Message = "O tópico {Destination} não existe no cluster; as mensagens permanecem pendentes e o relay nunca cria tópicos.")]
    internal static partial void KafkaTopicMissing(this ILogger logger, string destination);

    [LoggerMessage(EventId = 5141, Level = LogLevel.Warning, Message = "O broker recusou o registro destinado a {Destination}: {Reason}.")]
    internal static partial void KafkaRecordRejected(this ILogger logger, string destination, string reason);

    [LoggerMessage(EventId = 5142, Level = LogLevel.Warning, Message = "Falha ao publicar no tópico {Destination}; as mensagens permanecem pendentes.")]
    internal static partial void KafkaProduceCallFailed(this ILogger logger, string destination, Exception exception);

    [LoggerMessage(EventId = 5143, Level = LogLevel.Warning, Message = "O flush do produtor Kafka não concluiu no encerramento; relatórios pendentes foram abandonados.")]
    internal static partial void KafkaFlushFailed(this ILogger logger, Exception exception);
}
