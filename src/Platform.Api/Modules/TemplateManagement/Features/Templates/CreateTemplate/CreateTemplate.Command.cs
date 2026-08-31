namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

internal static partial class CreateTemplate
{
    internal sealed record Command(
        string Key,
        string Application,
        string Class,
        string OwnerTeam,
        string Purpose,
        string LegalBasis)
    {
        /// <summary>Locale the render fallback chain lands on; validation requires it before publish.</summary>
        public string? DefaultLocale { get; init; }

        /// <summary>Domains links and URL variables may point at. Absent or empty means no links allowed.</summary>
        public IReadOnlyList<string>? LinkDomainsAllowed { get; init; }
    }
}
