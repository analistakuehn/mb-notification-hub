using System.Text.Json.Serialization;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Features.Mutations;

internal static partial class PutLayoutVersionContent
{
    internal sealed record ContentEntry(
        string Channel,
        string Locale,
        string Body,
        string? BodyText,
        string BodyHash);

    internal sealed record Response
    {
        public required string LayoutKey { get; init; }

        public required int Version { get; init; }

        public required string Status { get; init; }

        public required string ContentHash { get; init; }

        public required string CreatedBy { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required IReadOnlyList<string> Editors { get; init; }

        public required IReadOnlyList<ContentEntry> Contents { get; init; }

        /// <summary>Travels in the ETag response header, never in the body.</summary>
        [JsonIgnore]
        public string EntityTag { get; init; } = string.Empty;

        internal static Response From(LayoutVersion version) => new()
        {
            LayoutKey = version.LayoutKey.Value,
            Version = version.Version,
            Status = version.Status.Canonical(),
            ContentHash = version.ContentHash,
            CreatedBy = version.CreatedBy,
            CreatedAt = version.CreatedAt,
            Editors = version.Editors,
            Contents = version.Contents
                .Select(content => new ContentEntry(
                    content.Channel.Value,
                    content.Locale.Value,
                    content.Body,
                    content.BodyText,
                    content.BodyHash))
                .ToList(),
            EntityTag = version.EntityTag,
        };
    }
}
