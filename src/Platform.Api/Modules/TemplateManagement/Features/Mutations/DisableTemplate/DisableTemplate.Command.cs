namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class DisableTemplate
{
    /// <summary>HTTP body: the mandatory justification recorded in the audit trail.</summary>
    internal sealed record Request(string Reason);

    internal sealed record Command(string Key, string Reason, string Actor);
}
