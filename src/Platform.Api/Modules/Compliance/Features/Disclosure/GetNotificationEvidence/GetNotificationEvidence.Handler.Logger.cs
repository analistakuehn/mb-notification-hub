namespace NotificationHub.Api.Modules.Compliance.Features.Disclosure;

internal static partial class GetNotificationEvidenceLogger
{
    [LoggerMessage(
        EventId = 7320,
        Level = LogLevel.Error,
        Message = "A trilha recusou o registro de divulgação do principal {Principal}; a resposta foi derrubada e nada foi divulgado.")]
    internal static partial void DisclosureRecordFailed(this ILogger logger, Exception exception, string principal);
}
