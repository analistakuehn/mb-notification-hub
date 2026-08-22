namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateLayout
{
    internal sealed record Command(
        string Key,
        string OwnerTeam)
    {
        /// <summary>Locale the layout render fallback chain lands on.</summary>
        public string? DefaultLocale { get; init; }
    }
}
