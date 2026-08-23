namespace NotificationHub.Api.Modules.Compliance.Infrastructure.Authorization;

internal static partial class AuditAccessHandlerLogger
{
    [LoggerMessage(
        EventId = 7300,
        Level = LogLevel.Warning,
        Message = "Acesso à superfície de auditoria negado para o principal {Principal} na rota {Route}: papel ausente.")]
    internal static partial void AuditAccessDenied(this ILogger logger, string principal, string route);

    [LoggerMessage(
        EventId = 7301,
        Level = LogLevel.Information,
        Message = "Acesso à superfície de auditoria autorizado para o principal {Principal} na rota {Route}.")]
    internal static partial void AuditAccessGranted(this ILogger logger, string principal, string route);
}
