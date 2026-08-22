using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetTemplate
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

        public required string Application { get; init; }

        public required string Class { get; init; }

        public required string OwnerTeam { get; init; }

        public required string Purpose { get; init; }

        public required string LegalBasis { get; init; }

        public required string Status { get; init; }

        public required IReadOnlyList<VersionSummary> Versions { get; init; }

        internal static Response From(Template template, IReadOnlyList<VersionSummary> versions) => new()
        {
            Key = template.Key.Value,
            Application = template.Application,
            Class = template.Class.Canonical(),
            OwnerTeam = template.OwnerTeam,
            Purpose = template.Purpose,
            LegalBasis = template.LegalBasis,
            Status = template.Status.Canonical(),
            Versions = versions,
        };
    }
}
