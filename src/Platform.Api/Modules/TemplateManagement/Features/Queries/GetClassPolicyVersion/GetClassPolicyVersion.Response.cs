using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Queries;

internal static partial class GetClassPolicyVersion
{
    internal sealed record Response
    {
        public required string Application { get; init; }

        public required string Class { get; init; }

        public required int Version { get; init; }

        public required string Status { get; init; }

        public required int SchemaVersion { get; init; }

        public required JsonElement Definition { get; init; }

        public required string ContentHash { get; init; }

        public required string CreatedBy { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required IReadOnlyList<string> Editors { get; init; }

        public DateTimeOffset? PublishedAt { get; init; }

        /// <summary>Travels in the ETag response header, never in the body.</summary>
        [JsonIgnore]
        public string EntityTag { get; init; } = string.Empty;

        internal static Response From(ClassPolicyVersion version) => new()
        {
            Application = version.Application,
            Class = version.Class.Canonical(),
            Version = version.Version,
            Status = version.Status.Canonical(),
            SchemaVersion = version.SchemaVersion,
            Definition = JsonProjections.ParseOrNull(version.DefinitionJson)!.Value,
            ContentHash = version.ContentHash,
            CreatedBy = version.CreatedBy,
            CreatedAt = version.CreatedAt,
            Editors = version.Editors,
            PublishedAt = version.PublishedAt,
            EntityTag = version.EntityTag,
        };
    }
}
