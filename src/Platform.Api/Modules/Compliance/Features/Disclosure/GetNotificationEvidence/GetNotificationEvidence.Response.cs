using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Compliance.Features.Disclosure;

internal static partial class GetNotificationEvidence
{
    /// <summary>
    /// The reconstruction of one notification, split into two blocks with
    /// different probative weight. <c>trail</c> holds chained links, each with
    /// its sequence, its hash and the hash before it, all of them rebuilt from
    /// the canonical text the chain covers. <c>state</c> holds domain
    /// projections of the modules that own the data, which no chain vouches for.
    /// Without the split an auditor cannot tell what the chain covers, and a
    /// projection would borrow credibility the chain never gave it.
    /// </summary>
    /// <remarks>
    /// One member a reader may expect is deliberately not declared, in any
    /// form: there is no read receipt, and it does not come back as an empty
    /// array either, because no table records one and an empty array would
    /// state that nobody read the message. Provider feedback is a different
    /// case now that it is recorded, so the attempt carries it and an empty
    /// list there asserts a fact.
    /// </remarks>
    internal sealed record Response
    {
        /// <summary>The public form of the identity that was reconstructed.</summary>
        public required string Id { get; init; }

        public required DisclosureView Disclosure { get; init; }

        public required TrailView Trail { get; init; }

        public required StateView State { get; init; }
    }

    /// <summary>
    /// What this very answer disclosed and where it cut. The cut matters: the
    /// list of previous accesses was read at <c>composedAt</c>, before the
    /// disclosure of this call was appended, so an auditor never reads their own
    /// footprint as somebody else's.
    /// </summary>
    internal sealed record DisclosureView
    {
        public required DateTimeOffset ComposedAt { get; init; }

        public required WindowView Window { get; init; }
    }

    /// <summary>The occurrence window every trail and ledger read of this answer used.</summary>
    internal sealed record WindowView
    {
        public required DateTimeOffset From { get; init; }

        public required DateTimeOffset To { get; init; }
    }

    /// <summary>
    /// The part of the answer the hash chain covers. Lifecycle links and
    /// disclosure links travel in separate members because they answer separate
    /// questions, and no link appears in both.
    /// </summary>
    internal sealed record TrailView
    {
        public required IReadOnlyList<LinkView> Links { get; init; }

        /// <summary>
        /// Rows inside the window, for these subjects, written before the chain
        /// existed and therefore covered by nothing. Nothing was fabricated for
        /// them; the count keeps their absence from the links visible.
        /// </summary>
        public required int UnchainedRows { get; init; }

        /// <summary>Disclosures of these subjects recorded before this call.</summary>
        public required IReadOnlyList<LinkView> PriorAccesses { get; init; }
    }

    /// <summary>
    /// One chained link. Every field was parsed out of <see cref="Canonical"/>,
    /// which travels verbatim so the reader can recompute the hash without
    /// trusting this response.
    /// </summary>
    internal sealed record LinkView
    {
        public required long Seq { get; init; }

        public required string Hash { get; init; }

        public required string PrevHash { get; init; }

        public required string Action { get; init; }

        public required string ActorType { get; init; }

        public required string ActorId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Application { get; init; }

        public required string EntityType { get; init; }

        public required string EntityId { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }

        public required JsonElement Details { get; init; }

        /// <summary>The exact text the hash covers.</summary>
        public required string Canonical { get; init; }
    }

    /// <summary>
    /// The part of the answer no chain covers: what each owning module holds
    /// today about the notification, the version it used, who approved that
    /// version, and the recipient's contact and consent history.
    /// </summary>
    internal sealed record StateView
    {
        public required NotificationView Notification { get; init; }

        public required IReadOnlyList<AttemptView> Attempts { get; init; }

        public required IReadOnlyList<PolicyEvaluationView> PolicyEvaluations { get; init; }

        /// <summary>
        /// The historical version the notification rendered. Absent when the
        /// catalog no longer resolves it, because answering with the version
        /// published today would not be a partial answer, it would be a wrong
        /// one.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TemplateVersionView? Template { get; init; }

        /// <summary>
        /// Approvals of that exact version. The table is append-only and sits
        /// outside the hash chain, which is why it is here and not in the trail.
        /// </summary>
        public required IReadOnlyList<ApprovalView> Approvals { get; init; }

        public required RecipientView Recipient { get; init; }

        /// <summary>
        /// The attachments the notification was accepted over, as the snapshot
        /// on its row froze them and as the owning module still records them.
        /// Always present as a block, because the block is what tells an empty
        /// set apart from a set nobody can name.
        /// </summary>
        public required AttachmentsView Attachments { get; init; }
    }

    /// <summary>
    /// What the notification row says about the attachments it was accepted
    /// over. Exactly one of the two members is ever written.
    /// <para>
    /// <c>accepted</c> present and empty states a fact, that the notification
    /// named no attachments. <c>accepted</c> missing states ignorance, and
    /// <c>unreadable</c> then names the shape of the defect in the stored
    /// document. An answer that reported the second as an empty array would
    /// tell an auditor that a notification carried nothing when what happened
    /// is that its set cannot be read.
    /// </para>
    /// </summary>
    internal sealed record AttachmentsView
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public IReadOnlyList<AcceptedAttachmentView>? Accepted { get; init; }

        /// <summary>
        /// Why the stored document does not read, from the closed vocabulary of
        /// the module that owns it. It names the shape of the defect and never
        /// quotes the document.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Unreadable { get; init; }
    }

    /// <summary>
    /// One accepted attachment: what the acceptance froze about it, and what
    /// the module that owns it still records about the bytes.
    /// <para>
    /// The name, the media type and the length appear here and are kept out of
    /// every other surface. They are producer data, and an operational line, a
    /// queue body or a published event is exactly where they must not surface;
    /// this route is a single authorized disclosure whose purpose is telling an
    /// auditor what went out, and there they are the answer.
    /// </para>
    /// </summary>
    internal sealed record AcceptedAttachmentView
    {
        /// <summary>The opaque identity of the attachment, as the claim received it.</summary>
        public required string Reference { get; init; }

        /// <summary>
        /// The handle the acceptance froze. It says which bytes were accepted,
        /// it says nothing on its own, and it is neither a coordinate nor a
        /// value that can be exchanged for content.
        /// </summary>
        public required string ContentIdentity { get; init; }

        public required string Name { get; init; }

        public required string MediaType { get; init; }

        public required long Length { get; init; }

        /// <summary>
        /// What the owning module still records about the content this member
        /// was accepted with. Absent when that module no longer answers for the
        /// handle, which is a statement about the record and never about the
        /// send: the members above still say what the notification carried.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public RecordedContentView? Recorded { get; init; }
    }

    /// <summary>
    /// The durable record of one accepted content: the proof of which bytes
    /// they were, and what the lifecycle of the attachment says about them.
    /// <para>
    /// The digest travels and the coordinates do not. A store, a key or a
    /// generation of the provider is capacity to reach bytes rather than proof
    /// of them, and this answer exists so that an auditor never has to be given
    /// one. The bytes themselves leave through no member here in any form.
    /// </para>
    /// <para>
    /// The record outlives the content. A sweep that takes the bytes of an
    /// abandoned attachment removes no row, so this block still answers
    /// afterwards and <c>state</c> is what says the content is gone.
    /// </para>
    /// </summary>
    internal sealed record RecordedContentView
    {
        /// <summary>
        /// The attachment the handle resolves to, as the owning module records
        /// it. It is that module's own answer, so a reader compares it with the
        /// reference the snapshot froze instead of inheriting one of the two.
        /// </summary>
        public required string Reference { get; init; }

        public required string Application { get; init; }

        public required string State { get; init; }

        /// <summary>Registered name of the digest computed over these bytes.</summary>
        public required string DigestAlgorithm { get; init; }

        /// <summary>The digest of the exact generation the handle names, in lowercase hex.</summary>
        public required string Digest { get; init; }

        /// <summary>The length measured in the pass that measured the digest.</summary>
        public required long DigestedLengthBytes { get; init; }

        /// <summary>When those bytes were captured and measured.</summary>
        public required DateTimeOffset CapturedAt { get; init; }

        /// <summary>Which check refused, or which verdict did not conclude.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ValidationDetail { get; init; }

        /// <summary>What the leading bytes were recognized as, when a signature matched them.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? DetectedContentType { get; init; }

        /// <summary>
        /// When the release over this exact generation was granted, and never
        /// the latest grant of the attachment: a revalidation writes a second
        /// grant over other bytes, and reporting it would date the approval of
        /// content this notification never carried.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? ReleasedAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? RevokedAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? RevocationReason { get; init; }
    }

    internal sealed record NotificationView
    {
        public required string Id { get; init; }

        public required string Application { get; init; }

        public required string RecipientId { get; init; }

        public required string Class { get; init; }

        public required string Status { get; init; }

        public required string TemplateKey { get; init; }

        /// <summary>The version the render used, re-stamped when publication moved under it.</summary>
        public required int TemplateVersion { get; init; }

        public required string RequestedBy { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        public required DateTimeOffset ExpiresAt { get; init; }

        /// <summary>
        /// What the producer actually asked for, with every sensitive value
        /// already masked at ingestion and irreversibly so. It is the business
        /// payload of the request and it answers repudiation; it leaves through
        /// this surface and through no other.
        /// </summary>
        public required JsonElement VariablesMasked { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? PolicyVersion { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? CorrelationId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? ReleaseAt { get; init; }
    }

    /// <summary>
    /// One delivery attempt, with acceptance and delivery kept apart.
    /// <c>sentAt</c> and <c>providerMessageId</c> assert that the provider took
    /// responsibility for the message. <c>deliveredAt</c> and
    /// <c>deliveryEvents</c> are the ones that speak about the destination:
    /// the first is the conclusion this hub applied, the second is what the
    /// provider reported, and they may disagree, because feedback can be
    /// recorded without moving an attempt that already reached a verdict.
    /// </summary>
    internal sealed record AttemptView
    {
        public required int Sequence { get; init; }

        public required string Channel { get; init; }

        public required string Status { get; init; }

        public required string ContentHashFull { get; init; }

        public required string ContentHashMasked { get; init; }

        public required DateTimeOffset CreatedAt { get; init; }

        /// <summary>
        /// Provider feedback recorded for this attempt, oldest first by the
        /// instant the provider says it happened. Always present: an empty
        /// array asserts that the store holds no feedback for this attempt,
        /// which is a fact, and no longer the absence of a place to record one.
        /// </summary>
        public required IReadOnlyList<DeliveryEventView> DeliveryEvents { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProviderKey { get; init; }

        /// <summary>Identity the provider gave the message when it accepted it.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ProviderMessageId { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorCode { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? FallbackDeadline { get; init; }

        /// <summary>Instant the provider accepted the message, never an arrival.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? SentAt { get; init; }

        /// <summary>
        /// Instant a provider confirmed the message reached the destination, in
        /// the provider's own clock. Absent means this hub applied no
        /// confirmation to the attempt; <c>deliveryEvents</c> is what says
        /// whether any feedback arrived at all.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? DeliveredAt { get; init; }

        /// <summary>Contact point the attempt targeted; described under the recipient block.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? ContactPointId { get; init; }

        /// <summary>Device registration the attempt targeted; described under the recipient block.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Guid? DeviceTokenId { get; init; }
    }

    /// <summary>
    /// One piece of provider feedback: what the provider reported about the
    /// attempt, when it says it happened, and under which identity it said it.
    /// The verified provider payload that the same row stores does not travel
    /// here in any form. It carries the destination in the clear, which is why
    /// it is sealed at rest, and this route discloses evidence to an auditor
    /// rather than reopening a contact value the rest of the answer masks.
    /// </summary>
    internal sealed record DeliveryEventView
    {
        public required string ProviderKey { get; init; }

        /// <summary>Identity of the event inside its provider, which is what deduplication claims.</summary>
        public required string ProviderEventId { get; init; }

        /// <summary>What the provider reported: sent, delivered, read, failed or bounced.</summary>
        public required string Kind { get; init; }

        /// <summary>
        /// Instant the provider says the event happened, in the provider's own
        /// clock. A provider may date an event backwards, so this is the claim
        /// of the feedback and not the instant this hub took it.
        /// </summary>
        public required DateTimeOffset OccurredAt { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? ErrorCode { get; init; }
    }

    /// <summary>
    /// One recorded rule decision with its evidence projected key by key. The
    /// raw document stays inside the module that owns the rule: it is free-form
    /// per rule, and serving it would freeze an internal shape as a public
    /// contract.
    /// </summary>
    internal sealed record PolicyEvaluationView
    {
        public required string Rule { get; init; }

        public required string Result { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Reason { get; init; }

        public required DateTimeOffset EvaluatedAt { get; init; }

        public required JsonElement Evidence { get; init; }

        /// <summary>
        /// Evidence keys the rule emitted and the projection does not cover, by
        /// name. A non-empty list is a defect of this surface, declared instead
        /// of hidden.
        /// </summary>
        public required IReadOnlyList<string> UndisclosedEvidenceKeys { get; init; }
    }

    internal sealed record TemplateVersionView
    {
        public required string Application { get; init; }

        public required string TemplateKey { get; init; }

        public required int Version { get; init; }

        public required string VersionStatus { get; init; }

        public required string TemplateStatus { get; init; }

        public required string Class { get; init; }

        public required string OwnerTeam { get; init; }

        public required string Purpose { get; init; }

        /// <summary>Legal basis of the identity this version belongs to.</summary>
        public required string LegalBasis { get; init; }

        public required IReadOnlyList<string> SensitiveVariables { get; init; }

        public required string ContentHash { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? PublishedAt { get; init; }

        /// <summary>Version this one republished, when a rollback created it.</summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? RolledBackFromVersion { get; init; }

        /// <summary>
        /// The layout reference the version declared, present whenever it
        /// declared one and whether or not <see cref="Layout"/> resolved. Its
        /// absence is the one way this block states that the message went out
        /// framed by nothing; a pin here with no <see cref="Layout"/> states
        /// that it was framed and that this answer cannot vouch for the frame.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LayoutPinView? LayoutPin { get; init; }

        /// <summary>
        /// The pinned layout with its own canonical hash. Absent when the
        /// version pinned none, and absent as well when the catalog withheld
        /// the one it pinned, which is why the pin above travels separately:
        /// without it the two read alike, and an auditor concludes the message
        /// carried no wrapper when the truth may be that it carried one whose
        /// hash nobody can produce.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public LayoutVersionView? Layout { get; init; }
    }

    /// <summary>The layout reference the version declared, key and number, resolved against nothing.</summary>
    internal sealed record LayoutPinView
    {
        public required string LayoutKey { get; init; }

        public required int Version { get; init; }
    }

    internal sealed record LayoutVersionView
    {
        public required string LayoutKey { get; init; }

        public required int Version { get; init; }

        public required string VersionStatus { get; init; }

        public required string ContentHash { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? PublishedAt { get; init; }
    }

    internal sealed record ApprovalView
    {
        public required string SubjectType { get; init; }

        public required string SubjectId { get; init; }

        public required int SubjectVersion { get; init; }

        public required string ContentHash { get; init; }

        public required string Role { get; init; }

        public required string ApproverOid { get; init; }

        public required DateTimeOffset ApprovedAt { get; init; }
    }

    /// <summary>
    /// The recipient side of the evidence. Contact values travel masked and
    /// device tokens do not travel at all, not even masked: a token is a
    /// credential, and no audit question is answered by holding one.
    /// </summary>
    internal sealed record RecipientView
    {
        public required string RecipientId { get; init; }

        public required IReadOnlyList<ContactPointView> ContactPoints { get; init; }

        public required IReadOnlyList<DeviceRegistrationView> Devices { get; init; }

        /// <summary>
        /// Every consent entry recorded inside the declared window, oldest
        /// first. The answer never states which entry was in force at the
        /// instant of the send: reading the ledger is the auditor's job, and
        /// the hub stating it would be the hub interpreting its own evidence.
        /// </summary>
        public required IReadOnlyList<ConsentEntryView> ConsentLedger { get; init; }
    }

    internal sealed record ContactPointView
    {
        public required Guid ContactPointId { get; init; }

        public required string Channel { get; init; }

        public required string MaskedValue { get; init; }

        public required bool Verified { get; init; }

        public required bool Active { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? RemovedAt { get; init; }
    }

    internal sealed record DeviceRegistrationView
    {
        public required Guid DeviceTokenId { get; init; }

        public required string Platform { get; init; }

        public required DateTimeOffset RegisteredAt { get; init; }

        public required DateTimeOffset LastSeenAt { get; init; }

        public required bool Active { get; init; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? AppVersion { get; init; }

        /// <summary>
        /// Instant provider feedback declared the token dead.
        /// </summary>
        /// <remarks>
        /// The reason the provider gave is **not** here and never will be. It
        /// is a trail fact, recorded by the lifecycle write as a link over this
        /// registration, and it travels in the trail block of this same answer.
        /// The state block states that an invalidation happened and when; the
        /// trail block states why. A reason column on this row would create a
        /// second home for one truth and exactly the drift between a column and
        /// the canonical text that the chain verification exists to catch.
        /// </remarks>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public DateTimeOffset? InvalidatedAt { get; init; }
    }

    internal sealed record ConsentEntryView
    {
        public required Guid ContactPointId { get; init; }

        public required string Channel { get; init; }

        public required string Purpose { get; init; }

        public required bool Granted { get; init; }

        public required string Source { get; init; }

        public required string ActorId { get; init; }

        public required string TermsVersion { get; init; }

        public required DateTimeOffset RecordedAt { get; init; }
    }
}
