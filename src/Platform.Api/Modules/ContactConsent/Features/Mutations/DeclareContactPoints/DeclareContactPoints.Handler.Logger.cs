namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareContactPointsLogger
{
    [LoggerMessage(EventId = 8000, Level = LogLevel.Information, Message = "Pontos de contato declarados para o destinatário {RecipientId}: {Added} novos, {Updated} atualizados, {Removed} removidos.")]
    internal static partial void ContactPointsDeclared(this ILogger logger, string recipientId, int added, int updated, int removed);

    [LoggerMessage(EventId = 8001, Level = LogLevel.Information, Message = "Declaração de pontos de contato sem mudança para o destinatário {RecipientId}.")]
    internal static partial void ContactPointsUnchanged(this ILogger logger, string recipientId);

    [LoggerMessage(EventId = 8002, Level = LogLevel.Warning, Message = "Conflito de escrita concorrente ao declarar contatos do destinatário {RecipientId}.")]
    internal static partial void ContactWriteConflict(this ILogger logger, string recipientId);
}
