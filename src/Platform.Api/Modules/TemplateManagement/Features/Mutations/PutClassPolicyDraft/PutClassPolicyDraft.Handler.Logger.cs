namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutClassPolicyDraftLogger
{
    [LoggerMessage(EventId = 4000, Level = LogLevel.Information, Message = "Rascunho v{Version} da política da classe {PolicyClass} da aplicação {Application} aberto.")]
    internal static partial void ClassPolicyDraftOpened(this ILogger logger, string application, string policyClass, int version);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "Rascunho v{Version} da política da classe {PolicyClass} da aplicação {Application} atualizado.")]
    internal static partial void ClassPolicyDraftUpdated(this ILogger logger, string application, string policyClass, int version);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Warning, Message = "Definição da política da classe {PolicyClass} da aplicação {Application} reprovada na validação estrutural com {FailedChecks} verificações reprovadas.")]
    internal static partial void ClassPolicyDraftBlocked(this ILogger logger, string application, string policyClass, int failedChecks);
}
