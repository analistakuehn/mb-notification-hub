using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PublishClassPolicyVersion
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
        string Application,
        string Class,
        int Version,
        string Status,
        int SchemaVersion,
        string ContentHash,
        DateTimeOffset PublishedAt,
        int? SupersededVersion)
    {
        internal static Response From(ClassPolicyVersion version, int? supersededVersion) => new(
            version.Application,
            version.Class.Canonical(),
            version.Version,
            version.Status.Canonical(),
            version.SchemaVersion,
            version.ContentHash,
            version.PublishedAt!.Value,
            supersededVersion);
    }
}
