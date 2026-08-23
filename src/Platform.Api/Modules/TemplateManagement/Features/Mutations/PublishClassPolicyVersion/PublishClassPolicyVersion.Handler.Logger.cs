namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishClassPolicyVersionLogger
{
    [LoggerMessage(EventId = 4010, Level = LogLevel.Information, Message = "Versão {Version} da política da classe {PolicyClass} da aplicação {Application} publicada. Versão substituída: {SupersededVersion}.")]
    internal static partial void ClassPolicyVersionPublished(this ILogger logger, string application, string policyClass, int version, int? supersededVersion);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Warning, Message = "Publicação da versão {Version} da política da classe {PolicyClass} da aplicação {Application} bloqueada pela validação com {FailedChecks} verificações reprovadas.")]
    internal static partial void ClassPolicyPublicationBlocked(this ILogger logger, string application, string policyClass, int version, int failedChecks);
}
