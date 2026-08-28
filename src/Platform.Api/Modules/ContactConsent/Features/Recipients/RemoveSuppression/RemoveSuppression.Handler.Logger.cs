namespace NotificationHub.Api.Modules.ContactConsent.Features.Recipients;

/// <summary>
/// Log of the manual reversal. Identifiers and actor only: the address behind
/// the contact point never reaches a log line.
/// </summary>
internal static partial class RemoveSuppressionLogger
{
    [LoggerMessage(
        EventId = 8036,
        Level = LogLevel.Warning,
        Message = "Supressão do ponto de contato {ContactPointId} do destinatário {RecipientId} "
            + "removida por {ActorId}; o canal volta a ser elegível.")]
    internal static partial void SuppressionRemoved(
        this ILogger logger, string recipientId, Guid contactPointId, string actorId);

    [LoggerMessage(
        EventId = 8037,
        Level = LogLevel.Information,
        Message = "Nenhuma supressão em vigor para o ponto de contato {ContactPointId} do "
            + "destinatário {RecipientId}; a remoção não produz efeito novo.")]
    internal static partial void SuppressionRemovalRepeated(
        this ILogger logger, string recipientId, Guid contactPointId);
}
