using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishLayoutVersion
{
    /// <summary>
    /// Discriminated outcome of a publish request. A blocked publication is a
    /// successful execution of the use case whose value carries the full
    /// validation report; only the transport decides how to express it.
    /// </summary>
    internal abstract record Outcome
    {
        private Outcome()
        {
        }

        internal sealed record Published(Response Response) : Outcome;

        internal sealed record Blocked(ValidationReport Report) : Outcome;
    }

    internal sealed record Response(
        string LayoutKey,
        int Version,
        string Status,
        string ContentHash,
        DateTimeOffset PublishedAt,
        int? SupersededVersion)
    {
        internal static Response From(LayoutVersion version, int? supersededVersion) => new(
            version.LayoutKey.Value,
            version.Version,
            version.Status.Canonical(),
            version.ContentHash,
            version.PublishedAt!.Value,
            supersededVersion);
    }
}
