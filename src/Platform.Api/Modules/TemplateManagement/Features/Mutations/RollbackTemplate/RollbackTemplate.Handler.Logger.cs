namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class RollbackTemplateLogger
{
    [LoggerMessage(EventId = 2090, Level = LogLevel.Information, Message = "Rollback do template {TemplateKey}: versão {Version} publicada como clone da versão {FromVersion}.")]
    internal static partial void RollbackPublished(this ILogger logger, string templateKey, int version, int fromVersion);

    [LoggerMessage(EventId = 2091, Level = LogLevel.Warning, Message = "Rollback do template {TemplateKey} para a versão {FromVersion} bloqueado pela validação com {FailedChecks} verificações reprovadas.")]
    internal static partial void RollbackBlocked(this ILogger logger, string templateKey, int fromVersion, int failedChecks);
}
