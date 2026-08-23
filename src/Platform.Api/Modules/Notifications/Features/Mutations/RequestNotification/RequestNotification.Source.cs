namespace NotificationHub.Api.Modules.Notifications.Features.Mutations;

internal static partial class RequestNotification
{
    /// <summary>
    /// Transport the request arrived through. It is not decoration: the audit
    /// trail records it on every ingress event, and it decides whether a
    /// template that declares sensitive variables may be requested at all,
    /// because a shared bus retains what a synchronous call never persists.
    /// </summary>
    internal enum IngestionSource
    {
        /// <summary>Synchronous producer call over the REST ingestion.</summary>
        Rest = 0,

        /// <summary>Asynchronous producer event over the corporate bus.</summary>
        Kafka = 1,
    }

    /// <summary>
    /// Where one request came from, as the trail must record it. The transport
    /// carries opaque coordinates the use case never interprets and always
    /// writes: on the bus they are what turns "a producer says it never asked"
    /// into a checkable claim, because topic, partition and offset point at
    /// the exact record the broker still holds, and the CloudEvent id names
    /// the envelope the producer minted.
    ///
    /// The trail is append-only and hash-chained, so a row written without
    /// them is never corrected, only supplemented; they are therefore part of
    /// the first write or they are lost for that request forever.
    /// </summary>
    internal sealed record IngestionOrigin
    {
        public required IngestionSource Source { get; init; }

        /// <summary>Topic the record came from; absent outside the bus.</summary>
        public string? Topic { get; init; }

        public int? Partition { get; init; }

        public long? Offset { get; init; }

        /// <summary>Envelope id the producer assigned; absent outside the bus.</summary>
        public string? EventId { get; init; }

        /// <summary>A synchronous call has no coordinates to record.</summary>
        internal static IngestionOrigin Rest { get; } = new() { Source = IngestionSource.Rest };

        /// <summary>
        /// The coordinates as the trail serializes them, or null when the
        /// transport has none. Null keeps the synchronous trail exactly as it
        /// was instead of stamping empty fields on every request.
        /// </summary>
        internal object? Coordinates()
            => Topic is null
                ? null
                : new
                {
                    topic = Topic,
                    partition = Partition,
                    offset = Offset,
                    eventId = EventId,
                };
    }
}
