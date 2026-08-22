using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class ListLayouts
{
    internal sealed record Item
    {
        public required string Key { get; init; }

        public required string OwnerTeam { get; init; }

        public required string Status { get; init; }

        public string? DefaultLocale { get; init; }

        internal static Item From(Layout layout) => new()
        {
            Key = layout.Key.Value,
            OwnerTeam = layout.OwnerTeam,
            Status = layout.Status.Canonical(),
            DefaultLocale = layout.DefaultLocale?.Value,
        };
    }

    internal sealed record Response(IReadOnlyList<Item> Items, string? NextCursor);
}
