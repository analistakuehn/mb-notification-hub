namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

internal static partial class ComposeMonthlyEvidenceLogger
{
    [LoggerMessage(
        EventId = 7335,
        Level = LogLevel.Information,
        Message = "Relatório mensal de evidências do mês {Month} arquivado em '{ObjectKey}' com digest {Digest}.")]
    internal static partial void MonthlyReportArchived(
        this ILogger logger,
        string month,
        string objectKey,
        string digest);
}
