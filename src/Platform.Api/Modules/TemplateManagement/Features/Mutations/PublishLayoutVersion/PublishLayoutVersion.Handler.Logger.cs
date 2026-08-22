namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishLayoutVersionLogger
{
    [LoggerMessage(EventId = 3060, Level = LogLevel.Information, Message = "Versão {Version} do layout {LayoutKey} publicada. Versão substituída: {SupersededVersion}.")]
    internal static partial void LayoutVersionPublished(this ILogger logger, string layoutKey, int version, int? supersededVersion);

    [LoggerMessage(EventId = 3061, Level = LogLevel.Warning, Message = "Publicação da versão {Version} do layout {LayoutKey} bloqueada pela validação com {FailedChecks} verificações reprovadas.")]
    internal static partial void LayoutPublicationBlocked(this ILogger logger, string layoutKey, int version, int failedChecks);
}
