namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;

internal static partial class IngressDeadLetterWriterLogger
{
    /// <summary>
    /// Emitted at the moment the dead-letter record is produced. The reason is
    /// its own field, not prose inside the message, because it is the
    /// dimension operations segments the dead-letter rate by: a refusal that
    /// never reaches the event topic (a malformed request with no subject to
    /// key an event on) is only visible here.
    /// </summary>
    [LoggerMessage(EventId = 7070, Level = LogLevel.Warning, Message = "Evento recusado gravado no dead-letter {DeadLetterTopic} por {Reason}; origem {SourceTopic}/{SourcePartition}/{SourceOffset}, produtor {Producer}, aplicação {Application}, corpo redigido: {Redacted}.")]
    internal static partial void DeadLetterProduced(this ILogger logger, string deadLetterTopic, string reason, string sourceTopic, int sourcePartition, long sourceOffset, string? producer, string? application, bool redacted);
}
