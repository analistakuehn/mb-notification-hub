namespace NotificationHub.Api.Modules.Audit.Infrastructure.Worm;

internal static partial class WormEvidenceArchiveLogger
{
    [LoggerMessage(
        EventId = 5026,
        Level = LogLevel.Information,
        Message = "Evidência arquivada em '{ObjectKey}' com {Length} bytes e digest {Digest}.")]
    internal static partial void EvidenceArchived(
        this ILogger logger,
        string objectKey,
        long length,
        string digest);

    [LoggerMessage(
        EventId = 5027,
        Level = LogLevel.Error,
        Message = "A chave '{ObjectKey}' já guarda evidência com digest diferente do recalculado; nada foi sobrescrito e a divergência precisa de análise humana.")]
    internal static partial void EvidenceDigestDiverged(this ILogger logger, string objectKey);

    [LoggerMessage(
        EventId = 5028,
        Level = LogLevel.Debug,
        Message = "A chave '{ObjectKey}' já guarda exatamente estes bytes; a reexecução não gravou nada.")]
    internal static partial void EvidenceAlreadyArchived(this ILogger logger, string objectKey);
}
