namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Events of the reading that hands the content over. None of them names the
/// handle, the store, the key, the generation of the provider, the file name
/// or the media type: the caller is about to reach a provider, and an
/// operational line is exactly where a coordinate and producer data must not
/// surface. The identifier of the recorded generation is this module's own row
/// identifier and is what an investigation starts from.
/// </summary>
internal static partial class RecordedAcceptedAttachmentContentLogger
{
    [LoggerMessage(
        EventId = 2460,
        Level = LogLevel.Warning,
        Message = "Identidade de conteúdo não reconhecida: o texto recebido não é um "
            + "manipulador emitido por este módulo e não nomeia geração alguma.")]
    internal static partial void AcceptedContentHandleNotMinted(this ILogger logger);

    [LoggerMessage(
        EventId = 2461,
        Level = LogLevel.Warning,
        Message = "A geração {GenerationId} não está mais registrada; o conteúdo aceito não "
            + "pôde ser entregue.")]
    internal static partial void AcceptedContentGenerationGone(
        this ILogger logger,
        Guid generationId);

    [LoggerMessage(
        EventId = 2462,
        Level = LogLevel.Error,
        Message = "O registro das gerações não pôde ser lido; nada foi estabelecido sobre o "
            + "conteúdo aceito.")]
    internal static partial void AcceptedContentRecordUnavailable(
        this ILogger logger,
        Exception exception);

    [LoggerMessage(
        EventId = 2463,
        Level = LogLevel.Warning,
        Message = "A custódia não entregou a geração {GenerationId} ({Status}); o conteúdo "
            + "aceito não pôde ser lido.")]
    internal static partial void AcceptedContentCustodyRefused(
        this ILogger logger,
        Guid generationId,
        string status);
}
