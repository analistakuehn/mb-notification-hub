using System.Text.Json;

namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    internal sealed record Command(
        string Application,
        string RecipientId,
        string Class,
        string TemplateKey,
        int TtlSeconds)
    {
        /// <summary>
        /// Locale the producer would prefer. Optional and inert: the render
        /// locale comes from the recipient profile or the template default, so
        /// requiring it would make a producer send a field the hub discards.
        /// It stays out of the idempotency payload for the same reason: a
        /// corrected or defaulted locale must never turn a retry of the same
        /// notification into a conflict.
        /// </summary>
        public string? Locale { get; init; }

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
