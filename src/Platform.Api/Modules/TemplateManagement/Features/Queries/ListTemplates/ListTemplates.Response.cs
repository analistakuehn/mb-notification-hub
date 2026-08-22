using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class ListTemplates
{
    internal sealed record Item
    {
        public required string Key { get; init; }

        public required string Application { get; init; }

        public required string Class { get; init; }

        public required string OwnerTeam { get; init; }

        public required string Purpose { get; init; }

        public required string LegalBasis { get; init; }

        public required string Status { get; init; }

        internal static Item From(Template template) => new()
        {
            Key = template.Key.Value,
            Application = template.Application,
            Class = template.Class.Canonical(),
            OwnerTeam = template.OwnerTeam,
            Purpose = template.Purpose,
            LegalBasis = template.LegalBasis,
            Status = template.Status.Canonical(),
        };
    }

    internal sealed record Response(IReadOnlyList<Item> Items, string? NextCursor);
}
