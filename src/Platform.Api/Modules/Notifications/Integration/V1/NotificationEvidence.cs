using System.Text.Json;

namespace NotificationHub.Api.Modules.Notifications.Integration.V1;

/// <summary>
/// Everything this module knows about one notification, for a consumer
/// reconstructing what happened. It is the state side of the evidence: domain
/// projections of rows this module owns, none of them covered by the audit hash
/// chain. Rendered content is not here in any form; it leaves only through the
/// dedicated reveal read, one attempt at a time.
/// </summary>
public sealed record NotificationEvidence
{
    public required Guid Id { get; init; }

    public required string Application { get; init; }

    public required string RecipientId { get; init; }

    public required string Class { get; init; }

    public required string Status { get; init; }

    public required string TemplateKey { get; init; }

    /// <summary>The version the render actually used, re-stamped when publication moved under it.</summary>
    public required int TemplateVersion { get; init; }

    public int? PolicyVersion { get; init; }

    public string? CorrelationId { get; init; }

    public required string RequestedBy { get; init; }

    public DateTimeOffset? ReleaseAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// The variables of the request with every sensitive value already masked,
    /// which is the only plaintext projection this module ever stores. It is
    /// business data and it answers repudiation: what the producer actually
    /// asked for. It leaves through this surface and through no other, so
    /// withholding it here would mean the projection leaves nowhere at all.
    /// </summary>
    public required JsonElement VariablesMasked { get; init; }

    public required IReadOnlyList<NotificationAttemptEvidence> Attempts { get; init; }

    public required IReadOnlyList<PolicyEvaluationEvidence> PolicyEvaluations { get; init; }
}

/// <summary>
/// One delivery attempt as evidence. The two content hashes travel, the content
/// never does. Acceptance and delivery are separate claims and travel in
/// separate members: <see cref="ProviderKey"/>, <see cref="ProviderMessageId"/>
/// and <see cref="SentAt"/> state that a provider took responsibility for the
/// message, while <see cref="DeliveredAt"/> and <see cref="DeliveryEvents"/>
/// state what the provider reported afterwards about the destination.
/// </summary>
public sealed record NotificationAttemptEvidence
{
    public required int Sequence { get; init; }

    public required string Channel { get; init; }

    public required string Status { get; init; }

    public string? ProviderKey { get; init; }

    public string? ProviderMessageId { get; init; }

    /// <summary>Contact point the attempt targeted; absent on push, whose targets are registrations.</summary>
    public Guid? ContactPointId { get; init; }

    /// <summary>Device registration a push attempt targeted; absent until the fan-out expanded it.</summary>
    public Guid? DeviceTokenId { get; init; }

    /// <summary>Canonical hash of the complete render, computed before any masking.</summary>
    public required string ContentHashFull { get; init; }

    /// <summary>Canonical hash of the masked render, which is the durable form.</summary>
    public required string ContentHashMasked { get; init; }

    public string? ErrorCode { get; init; }

    public DateTimeOffset? FallbackDeadline { get; init; }

    /// <summary>Instant the provider accepted the message.</summary>
    public DateTimeOffset? SentAt { get; init; }

    /// <summary>
    /// Instant a provider confirmed the message reached the destination, in the
    /// provider's own clock. Absent means no confirmation was applied to this
    /// attempt, which is a weaker statement than an empty
    /// <see cref="DeliveryEvents"/>: feedback can be stored without moving the
    /// attempt, and the two members then disagree on purpose.
    /// </summary>
    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>
    /// Every piece of provider feedback this module recorded for the attempt,
    /// oldest first by the instant the provider says it happened. An empty list
    /// asserts a fact rather than ignorance: the store holds no feedback for
    /// this attempt.
    /// </summary>
    public required IReadOnlyList<DeliveryEventEvidence> DeliveryEvents { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
/// One piece of provider feedback as evidence: what the provider said about one
/// attempt, when it says it happened, and under which identity it said it. The
/// verified provider payload the row also stores never travels here. It carries
/// the destination in the clear, it is sealed at rest for that reason, and this
/// is a disclosure surface: the payload is evidence held, not evidence served.
/// </summary>
public sealed record DeliveryEventEvidence
{
    public required string ProviderKey { get; init; }

    /// <summary>Identity of the event inside its provider, which is what deduplication claims.</summary>
    public required string ProviderEventId { get; init; }

    /// <summary>Durable spelling of what the provider reported.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Instant the provider says the event happened, in the provider's own
    /// clock and never this hub's. A provider may date an event backwards, so
    /// this is what the feedback claims rather than when the hub learned it.
    /// </summary>
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Code the provider gave for a failure; absent on feedback that reports no failure.</summary>
    public string? ErrorCode { get; init; }
}

/// <summary>
/// One recorded policy decision with its evidence reduced to the per-rule
/// allow-list this module owns. The raw document never crosses the boundary:
/// it is free-form per rule, and serving it would freeze an internal rule shape
/// as a public contract and break every consumer on the next rule adjustment.
/// </summary>
public sealed record PolicyEvaluationEvidence
{
    public required string Rule { get; init; }

    public required string Result { get; init; }

    /// <summary>Canonical rejection reason on defer and reject; absent on allow and filter.</summary>
    public string? Reason { get; init; }

    public required DateTimeOffset EvaluatedAt { get; init; }

    /// <summary>The projected evidence of the rule, allow-listed key by key.</summary>
    public required JsonElement Evidence { get; init; }

    /// <summary>
    /// Keys the rule emitted that the allow-list does not cover, by name only.
    /// A non-empty list means the projection is behind the rule: the value is
    /// withheld, and the gap is declared instead of disappearing.
    /// </summary>
    public required IReadOnlyList<string> UndisclosedEvidenceKeys { get; init; }
}

/// <summary>
/// The durable rendered content of one attempt, opened for disclosure. Only the
/// masked form ever leaves: it is what the store keeps once the send reached a
/// terminal verdict, and it is what the recorded masked hash vouches for. The
/// complete form is never served, not even while an in-flight attempt still
/// carries it, because a disclosure surface that could hand out a live one-time
/// code would defeat the reason the masking exists.
/// </summary>
public sealed record RevealedAttemptContent
{
    public required int Sequence { get; init; }

    public required string AttemptStatus { get; init; }

    public required string Channel { get; init; }

    public required string Locale { get; init; }

    public string? Subject { get; init; }

    public required string Body { get; init; }

    public string? BodyText { get; init; }

    /// <summary>Hash recorded for the masked form when the attempt was queued.</summary>
    public required string ContentHashMasked { get; init; }

    /// <summary>Hash recorded for the complete form, kept for confronting external evidence.</summary>
    public required string ContentHashFull { get; init; }

    /// <summary>
    /// The masked hash recomputed over exactly the fields served, with the
    /// canonical hasher the catalog publishes.
    /// </summary>
    public required string RecomputedContentHashMasked { get; init; }

    /// <summary>
    /// True while the stored envelope still carries the complete form beside the
    /// masked one, which means the attempt has not reached a terminal verdict.
    /// The served content is the masked form either way.
    /// </summary>
    public required bool CompleteFormStillStored { get; init; }
}
