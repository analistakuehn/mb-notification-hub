namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Release;

/// <summary>
/// Events of the release check. Neither of them names an attachment, a
/// generation, a content handle, a name, a media type or a length: the check
/// runs on the path that is about to reach a provider, and the fine detail of
/// which member refused belongs to the authorized lifecycle read rather than
/// to an operational line.
/// </summary>
internal static partial class RecordedAttachmentReleaseCheckLogger
{
    [LoggerMessage(
        EventId = 2450,
        Level = LogLevel.Warning,
        Message = "Conjunto aceito recusado na verificação de liberação: {WithheldCount} de "
            + "{MemberCount} anexo(s) não carregam liberação vigente sobre o conteúdo aceito.")]
    internal static partial void AcceptedSetWithheld(
        this ILogger logger,
        int withheldCount,
        int memberCount);

    [LoggerMessage(
        EventId = 2451,
        Level = LogLevel.Error,
        Message = "A verificação de liberação do conjunto aceito não concluiu: o registro "
            + "durável dos anexos não pôde ser lido, e nada foi afirmado sobre o conjunto.")]
    internal static partial void AcceptedSetCheckUnavailable(this ILogger logger, Exception exception);
}
