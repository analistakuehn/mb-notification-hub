using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Templates;

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

        public string? DefaultLocale { get; init; }

        public required IReadOnlyList<string> LinkDomainsAllowed { get; init; }

        /// <summary>
        /// One window of the version history, oldest first. It carries the whole
        /// history only while <see cref="VersionsTruncated"/> is false.
        /// </summary>
        public required IReadOnlyList<VersionSummary> Versions { get; init; }

        /// <summary>
        /// True when the history holds versions older than the ones listed here.
        /// </summary>
        public required bool VersionsTruncated { get; init; }

        /// <summary>
        /// Cursor for the next, older window of versions. Null when the listed
        /// versions reach the beginning of the history.
        /// </summary>
        public string? VersionsNextCursor { get; init; }

        internal static Response From(
            Template template,
            IReadOnlyList<VersionSummary> versions,
            bool versionsTruncated,
            string? versionsNextCursor) => new()
        {
            Key = template.Key.Value,
            Application = template.Application,
            Class = template.Class.Canonical(),
            OwnerTeam = template.OwnerTeam,
            Purpose = template.Purpose,
            LegalBasis = template.LegalBasis,
            Status = template.Status.Canonical(),
            DefaultLocale = template.DefaultLocale?.Value,
            LinkDomainsAllowed = template.LinkDomainsAllowed,
            Versions = versions,
            VersionsTruncated = versionsTruncated,
            VersionsNextCursor = versionsNextCursor,
        };
    }
}
