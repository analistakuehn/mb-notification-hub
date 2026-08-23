namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class DeclareConsentsLogger
{
    [LoggerMessage(EventId = 8010, Level = LogLevel.Information, Message = "Consentimentos declarados para o destinatário {RecipientId}: {Changed} registros novos no ledger.")]
    internal static partial void ConsentsDeclared(this ILogger logger, string recipientId, int changed);

    [LoggerMessage(EventId = 8011, Level = LogLevel.Information, Message = "Declaração de consentimentos sem mudança para o destinatário {RecipientId}.")]
    internal static partial void ConsentsUnchanged(this ILogger logger, string recipientId);

    [LoggerMessage(EventId = 8012, Level = LogLevel.Warning, Message = "Conflito de escrita concorrente ao declarar consentimentos do destinatário {RecipientId}.")]
    internal static partial void ConsentWriteConflict(this ILogger logger, string recipientId);
}
