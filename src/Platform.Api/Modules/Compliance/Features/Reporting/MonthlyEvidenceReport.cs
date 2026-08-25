using System.Text.Json;
using System.Text.Json.Serialization;

namespace NotificationHub.Api.Modules.Compliance.Features.Reporting;

/// <summary>
/// The archived shape of the monthly evidence report. It is a durable format:
/// once a month is archived the bytes can never be replaced, so the document
/// declares which window it covers and which version of this shape produced
/// it, and it carries no clock and no run identifier. A composition instant
/// inside the document would change the bytes on every rerun and turn an
/// idempotent job into a job that can never confirm what it already wrote.
/// </summary>
/// <remarks>
/// Absence is a statement here. A member this hub has a source for is always
/// declared, and an empty list under it asserts a fact: nothing of that kind
/// happened in the window. A member this hub has no source for is omitted
/// entirely, because an empty list would assert a fact nothing supports. The
/// sections currently omitted are named in <see cref="UnsourcedReportSections"/>.
/// </remarks>
internal sealed record MonthlyEvidenceReport
{
    /// <summary>
    /// Version of this shape. It travels in the object key as well, so a
    /// later version lands beside the earlier one instead of colliding with an
    /// object nobody can replace.
    /// </summary>
    internal const int CurrentFormatVersion = 1;

    /// <summary>Stable name of what this document is, for a reader holding only the bytes.</summary>
    internal const string ReportKind = "monthly-evidence";

    private static readonly JsonSerializerOptions Canonical = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public required int FormatVersion { get; init; }

    public required string Report { get; init; }

    public required ReportWindow Window { get; init; }

    /// <summary>Notifications requested inside the window, by canonical class.</summary>
    public required IReadOnlyList<ClassVolume> Volumes { get; init; }

    /// <summary>Delivery attempts queued inside the window, by channel.</summary>
    public required IReadOnlyList<ChannelOutcome> Channels { get; init; }

    public required RefusalSummary Refusals { get; init; }

    public required TrailActivity Trail { get; init; }

    /// <summary>Catalog, policy and configuration changes recorded in the trail.</summary>
    public required IReadOnlyList<GovernedChange> GovernedChanges { get; init; }

    /// <summary>Approvals granted inside the window; who vouched for which exact content.</summary>
    public required IReadOnlyList<Approval> Approvals { get; init; }

    /// <summary>Outcome of the hash-chain verification rounds, by partition.</summary>
    public required IReadOnlyList<ChainVerificationOutcome> ChainVerification { get; init; }

    /// <summary>
    /// Depth and age of the dead-letter queues. Omitted: no store in this hub
    /// holds them, and the operational metrics that will are not part of this
    /// platform yet. An empty list would state that no message was ever dead
    /// lettered, which nothing here knows.
    /// </summary>
    public IReadOnlyList<NamedCount>? DeadLetterQueues { get; init; }

    /// <summary>
    /// Provider-level failures: refusals by rate limiting, degraded channels
    /// and calls that never reached a provider. Omitted: they are a log entry
    /// and a queue disposition, never a row, so counting them is a question
    /// for operational metrics and not for this database.
    /// </summary>
    public IReadOnlyList<NamedCount>? ProviderFailures { get; init; }

    /// <summary>
    /// Activations of privileged access. Omitted: the elevation happens in the
    /// identity provider, which is outside this hub, so no source here can
    /// state one. The disclosures this hub does record are counted in the
    /// trail activity instead, and they are a different fact.
    /// </summary>
    public IReadOnlyList<NamedCount>? PrivilegedAccessActivations { get; init; }

    /// <summary>The exact bytes to archive: compact UTF-8, member order fixed by this shape.</summary>
    internal byte[] CanonicalBytes() => JsonSerializer.SerializeToUtf8Bytes(this, Canonical);

    /// <summary>The window the report covers, and the wait it observed before covering it.</summary>
    internal sealed record ReportWindow
    {
        /// <summary>Calendar month in UTC, spelled as the object key spells it.</summary>
        public required string Month { get; init; }

        public required DateTimeOffset FromInclusive { get; init; }

        public required DateTimeOffset ToExclusive { get; init; }

        /// <summary>
        /// How long after the end of the month the report was allowed to be
        /// composed, as an ISO-8601 duration. Delivery figures move backwards
        /// in time, so this is the part of the correction the report waited
        /// for, and a reader can tell how much of it the window still misses.
        /// </summary>
        public required string ReconciliationGrace { get; init; }
    }

    internal sealed record ClassVolume
    {
        public required string Class { get; init; }

        public required long Requested { get; init; }

        /// <summary>State of each requested notification when the window was read.</summary>
        public required IReadOnlyList<NamedCount> ByStatus { get; init; }
    }

    internal sealed record ChannelOutcome
    {
        public required string Channel { get; init; }

        /// <summary>
        /// What this hub can learn about a message on this channel. A channel
        /// whose providers report nothing afterwards is not a channel this hub
        /// measures badly; it is a channel where acceptance is the whole of
        /// what anyone will ever know.
        /// </summary>
        public required string DeliveryConfirmation { get; init; }

        public required long Attempts { get; init; }

        public required long AcceptedByProvider { get; init; }

        public required long Delivered { get; init; }

        public required long Bounced { get; init; }

        public required long Failed { get; init; }

        public required long Unknown { get; init; }

        public required long Pending { get; init; }

        /// <summary>
        /// Delivered over accepted. Omitted when the channel reports no
        /// delivery, and omitted when nothing was accepted: a rate with no
        /// denominator is not a small rate, and a zero here would read as a
        /// channel that delivered nothing.
        /// </summary>
        public double? DeliveryRate { get; init; }

        /// <summary>Bounced over accepted, omitted under the same rule as the delivery rate.</summary>
        public double? BounceRate { get; init; }
    }

    /// <summary>
    /// Why messages were refused, from the two places a refusal is recorded.
    /// They count different things and are never added together: one is a rule
    /// decision over an accepted request, the other is an event of the trail.
    /// </summary>
    internal sealed record RefusalSummary
    {
        /// <summary>Policy rule decisions that refused, by canonical reason.</summary>
        public required IReadOnlyList<NamedCount> ByPolicyReason { get; init; }

        /// <summary>Trail events that declared a reason, by action and then by reason.</summary>
        public required IReadOnlyList<ActionReasons> ByTrailAction { get; init; }
    }

    internal sealed record ActionReasons
    {
        public required string Action { get; init; }

        public required IReadOnlyList<NamedCount> ByReason { get; init; }
    }

    /// <summary>Everything the trail recorded in the window, counted by action.</summary>
    internal sealed record TrailActivity
    {
        public required IReadOnlyList<NamedCount> ByAction { get; init; }

        /// <summary>Rows of the window no chain covers; they predate the chain.</summary>
        public required long UnchainedRows { get; init; }
    }

    internal sealed record GovernedChange
    {
        public required long Seq { get; init; }

        public required string Action { get; init; }

        public required string EntityType { get; init; }

        public required string EntityId { get; init; }

        /// <summary>
        /// Whether a person or this platform itself caused the change. A
        /// switch that stops traffic can now be thrown automatically, so a
        /// reader that assumes a human behind every governed change is wrong.
        /// </summary>
        public required string ActorType { get; init; }

        public required string ActorId { get; init; }

        public string? Application { get; init; }

        public required DateTimeOffset OccurredAt { get; init; }

        /// <summary>Chain link that covers the event, so the change can be verified against the archive.</summary>
        public required string Hash { get; init; }
    }

    internal sealed record Approval
    {
        public required string SubjectType { get; init; }

        public required string SubjectId { get; init; }

        public required int SubjectVersion { get; init; }

        public required string ContentHash { get; init; }

        public required string Role { get; init; }

        public required string ApproverOid { get; init; }

        public required DateTimeOffset ApprovedAt { get; init; }
    }

    internal sealed record ChainVerificationOutcome
    {
        public required string Partition { get; init; }

        public required long IntactRounds { get; init; }

        public required long FailedRounds { get; init; }

        public DateTimeOffset? LastIntactAt { get; init; }

        public DateTimeOffset? LastFailureAt { get; init; }
    }

    /// <summary>One counted thing, named in the vocabulary of whoever counted it.</summary>
    internal sealed record NamedCount
    {
        public required string Name { get; init; }

        public required long Count { get; init; }
    }
}

/// <summary>
/// Sections this report does not declare, and the reason each one is absent
/// rather than empty. The names are the ones the document would use if a
/// source existed, so a later phase that gains the source knows exactly which
/// member it is filling and a check can assert that none of them appear.
/// </summary>
internal static class UnsourcedReportSections
{
    internal const string DeadLetterQueues = "deadLetterQueues";

    internal const string ProviderFailures = "providerFailures";

    internal const string PrivilegedAccessActivations = "privilegedAccessActivations";

    internal static IReadOnlyList<string> All { get; } =
        [DeadLetterQueues, ProviderFailures, PrivilegedAccessActivations];
}
