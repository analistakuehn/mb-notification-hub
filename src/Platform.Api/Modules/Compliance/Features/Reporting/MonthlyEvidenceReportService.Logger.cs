namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

internal static partial class MonthlyEvidenceReportServiceLogger
{
    [LoggerMessage(
        EventId = 7336,
        Level = LogLevel.Information,
        Message = "Relatório mensal de evidências iniciado com cadência de {Interval} e carência de reconciliação de {Grace}.")]
    internal static partial void MonthlyReportStarted(this ILogger logger, TimeSpan interval, TimeSpan grace);

    [LoggerMessage(
        EventId = 7337,
        Level = LogLevel.Information,
        Message = "Relatório mensal de evidências desabilitado por configuração; nenhum mês será composto por este host.")]
    internal static partial void MonthlyReportDisabled(this ILogger logger);

    [LoggerMessage(
        EventId = 7338,
        Level = LogLevel.Error,
        Message = "A composição do relatório mensal do mês {Month} foi recusada: {Reason}. Nada foi arquivado.")]
    internal static partial void MonthlyReportRefused(this ILogger logger, string month, string reason);

    [LoggerMessage(
        EventId = 7339,
        Level = LogLevel.Error,
        Message = "A rodada do relatório mensal falhou no mês {Month}; a próxima cadência tenta de novo.")]
    internal static partial void MonthlyReportRoundFailed(this ILogger logger, Exception exception, string month);
}
