using System.Text.Json;
using System.Text.Json.Serialization;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class CreateTemplateVersion
{
    internal sealed record ContentEntry(
        string Channel,
        string Locale,
        string? Subject,
        string Body,
        string? BodyText,
        string BodyHash);

    internal sealed record Response
    {
        public required string TemplateKey { get; init; }

        public required int Version { get; init; }

        public required string Status { get; init; }

        public required string ContentHash { get; init; }

        public required string CreatedBy { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required IReadOnlyList<string> Editors { get; init; }

        public JsonElement? VariablesSchema { get; init; }

        public required IReadOnlyList<ContentEntry> Contents { get; init; }

        /// <summary>Travels in the ETag response header, never in the body.</summary>
        [JsonIgnore]
        public string EntityTag { get; init; } = string.Empty;

        internal static Response From(TemplateVersion version) => new()
        {
            TemplateKey = version.TemplateKey.Value,
            Version = version.Version,
            Status = version.Status.Canonical(),
            ContentHash = version.ContentHash,
            CreatedBy = version.CreatedBy,
            CreatedAt = version.CreatedAt,
            Editors = version.Editors,
            VariablesSchema = JsonProjections.ParseOrNull(version.VariablesSchemaJson),
            Contents = version.Contents
                .Select(content => new ContentEntry(
                    content.Channel.Value,
                    content.Locale.Value,
                    content.Subject,
                    content.Body,
                    content.BodyText,
                    content.BodyHash))
                .ToList(),
            EntityTag = version.EntityTag,
        };
    }
}
