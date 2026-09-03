namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Events of the witness that settles the bytes one attempt submitted. None of
/// them names a handle, a store, a key, a generation of the provider, a file
/// name, a media type or a digest in any spelling: the witness runs on the
/// path that just reached a provider, and an operational line is exactly where
/// a coordinate, producer data and the proof of the bytes must not surface.
/// <para>
/// The identifier of the recorded generation does appear, and only on the
/// lines that report a member which did not hold. It is this module's own row
/// identifier, it is what an investigation starts from, and it is the same
/// value the reading that hands the content over already puts on this line.
/// Without it a divergence would be a count with nothing behind it, and a
/// count is not something anyone can go and look at.
/// </para>
/// </summary>
internal static partial class RecordedAttachmentSubmissionWitnessLogger
{
    [LoggerMessage(
        EventId = 2470,
        Level = LogLevel.Information,
        Message = "Testemunha dos bytes submetidos conferida: os {MemberCount} anexo(s) do "
            + "conjunto saíram com exatamente os bytes sob os quais foram liberados.")]
    internal static partial void SubmittedBytesMatched(this ILogger logger, int memberCount);

    [LoggerMessage(
        EventId = 2471,
        Level = LogLevel.Error,
        Message = "Testemunha dos bytes submetidos divergente: {DivergentCount} de "
            + "{MemberCount} anexo(s) saíram com bytes que não são os bytes liberados.")]
    internal static partial void SubmittedBytesDiverged(
        this ILogger logger,
        int divergentCount,
        int memberCount);

    [LoggerMessage(
        EventId = 2472,
        Level = LogLevel.Error,
        Message = "Os bytes submetidos do anexo da geração {GenerationId} não conferem com "
            + "os bytes registrados na captura daquela geração.")]
    internal static partial void SubmittedMemberDiverged(this ILogger logger, Guid generationId);

    [LoggerMessage(
        EventId = 2473,
        Level = LogLevel.Warning,
        Message = "Identidade de conteúdo não reconhecida na testemunha: o texto recebido não "
            + "é um manipulador emitido por este módulo e nada foi afirmado sobre os bytes "
            + "submetidos.")]
    internal static partial void SubmittedHandleNotMinted(this ILogger logger);

    [LoggerMessage(
        EventId = 2474,
        Level = LogLevel.Warning,
        Message = "A geração {GenerationId} não está mais registrada; nada foi afirmado sobre "
            + "os bytes submetidos do conjunto.")]
    internal static partial void SubmittedGenerationGone(this ILogger logger, Guid generationId);

    [LoggerMessage(
        EventId = 2475,
        Level = LogLevel.Error,
        Message = "O registro das gerações não pôde ser lido; a testemunha dos bytes "
            + "submetidos não concluiu e nada foi afirmado sobre o conjunto.")]
    internal static partial void SubmittedWitnessUnavailable(this ILogger logger, Exception exception);
}
