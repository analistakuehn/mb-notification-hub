namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Consuming;

internal static partial class ContactIngestionDeadLetterWriterLogger
{
    /// <summary>
    /// Emitted at the moment the dead-letter record is produced. The reason
    /// and the source are their own fields, not prose inside the message,
    /// because they are the dimensions operations segments the dead-letter
    /// rate by when one emitting system starts publishing records this hub
    /// cannot apply.
    /// </summary>
    [LoggerMessage(EventId = 8040, Level = LogLevel.Warning, Message = "Declaração recusada gravada no dead-letter {DeadLetterTopic} por {Reason}; origem {SourceTopic}/{SourcePartition}/{SourceOffset}, emissor {EventSource}, tipo {EventType}.")]
    internal static partial void ContactDeadLetterProduced(this ILogger logger, string deadLetterTopic, string reason, string sourceTopic, int sourcePartition, long sourceOffset, string? eventSource, string? eventType);
}
