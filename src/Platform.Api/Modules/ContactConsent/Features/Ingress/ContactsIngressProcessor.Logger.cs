namespace NotificationHub.Api.Modules.ContactConsent.Features.Ingress;

internal static partial class ContactsIngressProcessorLogger
{
    [LoggerMessage(EventId = 8050, Level = LogLevel.Information, Message = "Declaração {EventType} de {Topic}/{Partition}/{Offset} aplicada ao destinatário {RecipientId}.")]
    internal static partial void DeclarationApplied(this ILogger logger, string topic, int partition, long offset, string eventType, string recipientId);

    [LoggerMessage(EventId = 8051, Level = LogLevel.Information, Message = "Registro {Topic}/{Partition}/{Offset} já estava liquidado; nenhum efeito novo.")]
    internal static partial void DeclarationRedelivered(this ILogger logger, string topic, int partition, long offset);

    [LoggerMessage(EventId = 8052, Level = LogLevel.Warning, Message = "Registro {Topic}/{Partition}/{Offset} perdeu a corrida de escrita do destinatário {RecipientId}; a partição espera para reprocessar.")]
    internal static partial void DeclarationConflicted(this ILogger logger, string topic, int partition, long offset, string recipientId);
}
