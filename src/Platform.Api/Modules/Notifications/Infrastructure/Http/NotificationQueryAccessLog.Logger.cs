namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

internal static partial class NotificationQueryAccessLogLogger
{
    [LoggerMessage(
        EventId = 7150,
        Level = LogLevel.Information,
        Message = "Consulta de notificações atendida para o principal {Principal} na rota {Route} sobre o sujeito {SubjectType} {SubjectId}.")]
    internal static partial void NotificationQueryServed(
        this ILogger logger,
        string principal,
        string route,
        string subjectType,
        string subjectId);
}
