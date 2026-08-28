namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class ValidateClassPolicyVersionLogger
{
    [LoggerMessage(EventId = 4020, Level = LogLevel.Information, Message = "Versão {Version} da política da classe {PolicyClass} da aplicação {Application} validada. Aprovada: {Passed}, verificações reprovadas: {FailedChecks}.")]
    internal static partial void ClassPolicyVersionValidated(this ILogger logger, string application, string policyClass, int version, bool passed, int failedChecks);
}
