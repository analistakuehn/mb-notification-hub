namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

internal static partial class CreateLayoutVersion
{
    /// <summary>Optional HTTP body: clone source for the new draft.</summary>
    internal sealed record Request(int? FromVersion);

    internal sealed record Command(string Key, int? FromVersion, string Actor);
}
