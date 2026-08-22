namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplate
{
    internal sealed record Command(
        string Key,
        string Application,
        string Class,
        string OwnerTeam,
        string Purpose,
        string LegalBasis);
}
