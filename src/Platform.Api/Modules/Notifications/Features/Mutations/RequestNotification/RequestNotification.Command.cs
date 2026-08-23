using System.Text.Json;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    internal sealed record Command(
        string Application,
        string RecipientId,
        string Class,
        string TemplateKey,
        string Locale,
        int TtlSeconds)
    {
        /// <summary>Variables object validated against the published schema; absent or JSON null means none.</summary>
        public JsonElement? Variables { get; init; }

        /// <summary>Channel preference hint; the published policy decides, never this list.</summary>
        public IReadOnlyList<string>? ChannelsHint { get; init; }

        public string? CorrelationId { get; init; }

        /// <summary>Producer-side context; stored nowhere at ingestion, but part of the idempotency payload.</summary>
        public JsonElement? Metadata { get; init; }

        /// <summary>Deferred release instant (ISO 8601).</summary>
        public DateTimeOffset? ScheduledAt { get; init; }
    }
}
