namespace NotificationHub.Api.Modules.Compliance.Features.Disclosure;

internal static partial class GetAttemptContentLogger
{
    [LoggerMessage(
        EventId = 7330,
        Level = LogLevel.Error,
        Message = "A trilha recusou o registro de divulgação de conteúdo do principal {Principal}; a resposta foi derrubada e nenhum conteúdo foi divulgado.")]
    internal static partial void ContentDisclosureRecordFailed(
        this ILogger logger,
        Exception exception,
        string principal);
}
