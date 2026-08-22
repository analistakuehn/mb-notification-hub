using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class RollbackTemplate
{
    /// <summary>
    /// Discriminated outcome of a rollback request. A rollback blocked by the
    /// validation catalog is a successful execution whose value carries the
    /// full report; only the transport decides how to express it.
    /// </summary>
    internal abstract record Outcome
    {
        private Outcome()
        {
        }

        internal sealed record RolledBack(Response Response) : Outcome;

        internal sealed record Blocked(ValidationReport Report) : Outcome;
    }

    internal sealed record Response(
        string TemplateKey,
        int Version,
        string Status,
        int RolledBackFrom,
        string ContentHash,
        DateTimeOffset PublishedAt,
        int? SupersededVersion)
    {
        internal static Response From(TemplateVersion version, int? supersededVersion) => new(
            version.TemplateKey.Value,
            version.Version,
            version.Status.Canonical(),
            version.RolledBackFrom!.Value,
            version.ContentHash,
            version.PublishedAt!.Value,
            supersededVersion);
    }
}
