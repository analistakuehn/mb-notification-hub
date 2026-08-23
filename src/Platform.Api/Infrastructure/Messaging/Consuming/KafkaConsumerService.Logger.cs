namespace NotificationHub.Api.Infrastructure.Messaging.Consuming;

internal static partial class KafkaConsumerServiceLogger
{
    [LoggerMessage(EventId = 5160, Level = LogLevel.Information, Message = "Consumo do barramento iniciado nos tópicos {Topics} pelo grupo {GroupId}.")]
    internal static partial void KafkaConsumerStarted(this ILogger logger, string topics, string groupId);

    [LoggerMessage(EventId = 5161, Level = LogLevel.Error, Message = "Alarme: o grupo {GroupId} não assina o tópico porque a pré-condição do papel não vale: {Reason}.")]
    internal static partial void KafkaConsumerGateClosed(this ILogger logger, string groupId, string reason);

    [LoggerMessage(EventId = 5162, Level = LogLevel.Warning, Message = "A passagem de consumo do grupo {GroupId} falhou; as mensagens permanecem no tópico.")]
    internal static partial void KafkaConsumePassFailed(this ILogger logger, string groupId, Exception exception);

    [LoggerMessage(EventId = 5163, Level = LogLevel.Warning, Message = "Registro {Topic}/{Partition}/{Offset} enviado ao dead-letter: {Reason}.")]
    internal static partial void KafkaRecordDeadLettered(this ILogger logger, string topic, int partition, long offset, string reason);

    [LoggerMessage(EventId = 5164, Level = LogLevel.Warning, Message = "Falha transitória no registro {Topic}/{Partition}/{Offset} após {Attempts} tentativas em processo; o offset não avança.")]
    internal static partial void KafkaRecordFailedTransiently(this ILogger logger, string topic, int partition, long offset, int attempts, Exception exception);

    [LoggerMessage(EventId = 5165, Level = LogLevel.Error, Message = "Alarme: partição {Topic}/{Partition} pausada por {PauseSeconds}s após falha persistente: {Reason}.")]
    internal static partial void KafkaPartitionPaused(this ILogger logger, string topic, int partition, string reason, int pauseSeconds);

    [LoggerMessage(EventId = 5166, Level = LogLevel.Information, Message = "Partição {Topic}/{Partition} retomada.")]
    internal static partial void KafkaPartitionResumed(this ILogger logger, string topic, int partition);
}
