namespace NotificationHub.Api.Modules.Audit.Infrastructure.Export;

internal static partial class AuditExporterLogger
{
    [LoggerMessage(EventId = 5020, Level = LogLevel.Information, Message = "Export {ExportType} da partição {Partition} gravado com {ChainedCount} eventos encadeados e {UnchainedCount} pré-cadeia em '{ManifestKey}'.")]
    internal static partial void ExportWritten(
        this ILogger logger,
        string exportType,
        string partition,
        int chainedCount,
        int unchainedCount,
        string manifestKey);

    [LoggerMessage(EventId = 5021, Level = LogLevel.Information, Message = "Chave pública de atestado '{KeyId}' arquivada em '{ObjectKey}'.")]
    internal static partial void PublicKeyArchived(this ILogger logger, string keyId, string objectKey);

    [LoggerMessage(EventId = 5022, Level = LogLevel.Information, Message = "Export diário da partição {Partition} do dia {Day} concluído; faixa de seq {SeqMin}-{SeqMax}.")]
    internal static partial void DailyExportCompleted(
        this ILogger logger,
        string partition,
        string day,
        long seqMin,
        long seqMax);

    [LoggerMessage(EventId = 5023, Level = LogLevel.Warning, Message = "Export da partição {Partition} falhou; nenhuma etapa destrutiva foi executada.")]
    internal static partial void ExportFailed(this ILogger logger, string partition, Exception exception);
}
