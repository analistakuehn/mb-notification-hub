namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Http;

internal static partial class AuditAccessLogLogger
{
    [LoggerMessage(
        EventId = 7310,
        Level = LogLevel.Warning,
        Message = "Leitura de auditoria do principal {Principal} na rota {Route} não encontrou o sujeito solicitado.")]
    internal static partial void AuditSubjectNotFound(this ILogger logger, string principal, string route);

    [LoggerMessage(
        EventId = 7311,
        Level = LogLevel.Warning,
        Message = "Volume de divulgação de conteúdo do principal {Principal} atingiu {Disclosures} aberturas na janela de {WindowMinutes} minutos.")]
    internal static partial void ContentDisclosureVolumeReached(
        this ILogger logger,
        string principal,
        int disclosures,
        int windowMinutes);
}
