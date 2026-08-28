using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Notifications.Features.History;

internal static partial class GetNotification
{
    /// <summary>
    /// Aggregated view of one notification. Three rules govern the shape.
    /// Members that always exist are always present, even as an empty array,
    /// because the status explains an empty one. Members whose value is
    /// genuinely absent are omitted, because a null would claim the phase
    /// looked and found nothing. Members whose source does not exist in this
    /// phase are not declared at all: delivery events and the read receipt
    /// arrive with the delivery tracker, and an empty array now would assert
    /// something no table can support.
    /// </summary>
    /// <remarks>
    /// What never leaves through here: the rendered content in any form (only
    /// its two hashes travel) and the masked variables projection, which still
    /// carries business data and belongs to the audit surface, behind the
    /// audit role and its own trail.
    /// </remarks>
    internal sealed record Response
    {
        public required string Id { get; init; }

        public required string Application { get; init; }

        public required string Class { get; init; }

        public required string Status { get; init; }

        public required string TemplateKey { get; init; }

        public required int TemplateVersion { get; init; }

        public required string RequestedBy { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }

        /// <summary>Absent until the policy stage ruled the notification.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PolicyVersion { get; init; }

        /// <summary>Absent unless the producer asked for a deferred release.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? ReleaseAt { get; init; }

        public required IReadOnlyList<Evaluation> PolicyEvaluations { get; init; }

        public required IReadOnlyList<Attempt> Attempts { get; init; }
    }

    /// <summary>
    /// One recorded policy decision. The reason belongs to the canonical
    /// rejection catalog, which is a closed vocabulary; the rule evidence stays
    /// on the audit surface, because it is the trail's payload and not part of
    /// what a support read needs.
    /// </summary>
    internal sealed record Evaluation
    {
        public required string Rule { get; init; }

        public required string Result { get; init; }

        /// <summary>Absent when the rule allowed or only filtered channels.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }

        public required DateTimeOffset EvaluatedAt { get; init; }
    }

    /// <summary>
    /// One delivery attempt. The error code is the open vocabulary of delivery
    /// failure, never the canonical rejection catalog: the two answer different
    /// questions and never share a member.
    /// </summary>
    internal sealed record Attempt
    {
        public required int Sequence { get; init; }

        public required string Channel { get; init; }

        public required string Status { get; init; }

        public required string ContentHashFull { get; init; }

        public required string ContentHashMasked { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        /// <summary>Absent until a dispatcher claims the attempt.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProviderKey { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProviderMessageId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorCode { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? FallbackDeadline { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? SentAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? DeliveredAt { get; init; }

        /// <summary>Absent on a push attempt the fan-out has not expanded yet.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Target? Target { get; init; }
    }

    /// <summary>
    /// Where an attempt was aimed. A contact channel exposes the masked value
    /// its owning module computed and whether the point is still active; push
    /// exposes the platform and the registration identity, never the routing
    /// token, not even masked, because a token is a credential and not an
    /// address a human confirms.
    /// </summary>
    internal sealed record Target
    {
        internal const string ContactPointKind = "contact-point";
        internal const string DeviceKind = "device";

        public required string Kind { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? ContactPointId { get; init; }

        /// <summary>Absent when the contact directory could not answer.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Masked { get; init; }

        /// <summary>
        /// False when the target is no longer usable: a contact point removed
        /// after the send, or a device registration the provider feedback
        /// invalidated. Absent when the contact directory could not answer,
        /// which is a different fact from an inactive target.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public bool? Active { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? DeviceTokenId { get; init; }

        /// <summary>Absent when the registration is no longer active.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Platform { get; init; }
    }
}
