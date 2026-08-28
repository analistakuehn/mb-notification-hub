using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.ClassPolicies;

internal static partial class GetClassPolicy
{
    internal sealed record VersionDetail(
        int Version,
        string Status,
        int SchemaVersion,
        JsonElement Definition,
        string ContentHash,
        string CreatedBy,
        DateTimeOffset CreatedAt,
        IReadOnlyList<string> Editors,
        DateTimeOffset? PublishedAt)
    {
        internal static VersionDetail From(ClassPolicyVersion version) => new(
            version.Version,
            version.Status.Canonical(),
            version.SchemaVersion,
            JsonProjections.ParseOrNull(version.DefinitionJson)!.Value,
            version.ContentHash,
            version.CreatedBy,
            version.CreatedAt,
            version.Editors,
            version.PublishedAt);
    }

    /// <summary>The combined view an editor works from: what runs and what is being drafted.</summary>
    internal sealed record Response
    {
        public required string Application { get; init; }

        public required string Class { get; init; }

        public required VersionDetail? Published { get; init; }

        public required VersionDetail? Draft { get; init; }

        /// <summary>Travels in the ETag response header, never in the body.</summary>
        [JsonIgnore]
        public string? DraftEntityTag { get; init; }
    }
}
