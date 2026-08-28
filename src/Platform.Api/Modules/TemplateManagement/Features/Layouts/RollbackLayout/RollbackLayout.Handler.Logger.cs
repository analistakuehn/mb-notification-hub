namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class RollbackLayoutLogger
{
    [LoggerMessage(EventId = 3090, Level = LogLevel.Information, Message = "Rollback do layout {LayoutKey}: versão {Version} publicada como clone da versão {FromVersion}.")]
    internal static partial void LayoutRollbackPublished(this ILogger logger, string layoutKey, int version, int fromVersion);

    [LoggerMessage(EventId = 3091, Level = LogLevel.Warning, Message = "Rollback do layout {LayoutKey} para a versão {FromVersion} bloqueado pela validação com {FailedChecks} verificações reprovadas.")]
    internal static partial void LayoutRollbackBlocked(this ILogger logger, string layoutKey, int fromVersion, int failedChecks);
}
