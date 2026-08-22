using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetLayout
{
    internal sealed record VersionSummary(
        int Version,
        string Status,
        string ContentHash,
        string CreatedBy,
        DateTimeOffset CreatedAt);

    internal sealed record Response
    {
        public required string Key { get; init; }

        public required string OwnerTeam { get; init; }

        public required string Status { get; init; }

        public string? DefaultLocale { get; init; }

        public required IReadOnlyList<VersionSummary> Versions { get; init; }

        internal static Response From(Layout layout, IReadOnlyList<VersionSummary> versions) => new()
        {
            Key = layout.Key.Value,
            OwnerTeam = layout.OwnerTeam,
            Status = layout.Status.Canonical(),
            DefaultLocale = layout.DefaultLocale?.Value,
            Versions = versions,
        };
    }
}
