namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class RollbackLayout
{
    /// <summary>HTTP body: the previously published version to republish.</summary>
    internal sealed record Request(int ToVersion);

    internal sealed record Command(string Key, int ToVersion, string Actor);
}
