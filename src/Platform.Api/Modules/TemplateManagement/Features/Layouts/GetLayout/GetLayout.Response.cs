using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Layouts;

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
            Layout layout,
            IReadOnlyList<VersionSummary> versions,
            bool versionsTruncated,
            string? versionsNextCursor) => new()
        {
            Key = layout.Key.Value,
            OwnerTeam = layout.OwnerTeam,
            Status = layout.Status.Canonical(),
            DefaultLocale = layout.DefaultLocale?.Value,
            Versions = versions,
            VersionsTruncated = versionsTruncated,
            VersionsNextCursor = versionsNextCursor,
        };
    }
}
