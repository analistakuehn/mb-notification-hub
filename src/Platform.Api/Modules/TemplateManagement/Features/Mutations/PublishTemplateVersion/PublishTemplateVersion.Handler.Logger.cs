namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishTemplateVersionLogger
{
    [LoggerMessage(EventId = 2060, Level = LogLevel.Information, Message = "Versão {Version} do template {TemplateKey} publicada. Versão substituída: {SupersededVersion}.")]
    internal static partial void VersionPublished(this ILogger logger, string templateKey, int version, int? supersededVersion);

    [LoggerMessage(EventId = 2061, Level = LogLevel.Warning, Message = "Publicação da versão {Version} do template {TemplateKey} bloqueada pela validação com {FailedChecks} verificações reprovadas.")]
    internal static partial void PublicationBlocked(this ILogger logger, string templateKey, int version, int failedChecks);
}
