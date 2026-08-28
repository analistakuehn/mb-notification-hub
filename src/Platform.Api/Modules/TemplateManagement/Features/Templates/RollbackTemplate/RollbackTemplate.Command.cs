namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class RollbackTemplate
{
    /// <summary>HTTP body: the previously published version to republish.</summary>
    internal sealed record Request(int ToVersion);

    internal sealed record Command(string Key, int ToVersion, string Actor);
}
