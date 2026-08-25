namespace NotificationHub.Api.Modules.Audit.Integration.V1;

/// <summary>
/// Read surface of the trail for a periodic evidence composer. Every answer is
/// bounded by an occurrence window, because the trail is partitioned by month
/// and its indexes are local to each partition: a read without a window is a
/// scan of every month that ever existed.
/// </summary>
/// <remarks>
/// The split between the two kinds of answer is deliberate. Counts come from
/// the queryable columns, which is what they exist for; every individual event
/// this surface hands over is rebuilt from the canonical text the hash covers,
/// so an auditor reading a change out of a report is reading the bytes the
/// chain vouches for.
/// </remarks>
public interface IAuditPeriodEvidence
{
    /// <summary>
    /// Everything the trail can say about one window without naming a subject.
    /// An inverted or unbounded window fails as an argument exception, in the
    /// same posture as the rest of this module's contracts.
    /// </summary>
    Task<AuditPeriodEvidence> SummarizeAsync(
        DateTimeOffset fromInclusive,
        DateTimeOffset toExclusive,
        CancellationToken cancellationToken);
}

/// <summary>
/// Actions whose events change the governed catalog, the class policies, or a
/// switch that stops traffic. They are the changes a periodic review is there
/// to look at, and the set is published so a composer never has to guess which
/// of the vocabulary's actions matter.
/// </summary>
public static class AuditGovernedChangeActions
{
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        AuditActions.TemplateCreated,
        AuditActions.TemplateVersionPublished,
        AuditActions.TemplateDeprecated,
        AuditActions.TemplateDisabled,
        AuditActions.TemplateRollback,
        AuditActions.LayoutCreated,
        AuditActions.LayoutVersionPublished,
        AuditActions.LayoutDeprecated,
        AuditActions.LayoutDisabled,
        AuditActions.LayoutRollback,
        AuditActions.ClassPolicyVersionPublished,
        AuditActions.KillSwitchChanged,
    };
}

/// <summary>What the trail holds about one window, with no subject asked for.</summary>
public sealed record AuditPeriodEvidence
{
    public required DateTimeOffset FromInclusive { get; init; }

    public required DateTimeOffset ToExclusive { get; init; }

    /// <summary>Every action recorded inside the window and how many times, ordered by action.</summary>
    public required IReadOnlyList<AuditActionCount> ActionCounts { get; init; }

    /// <summary>
    /// Counts of each distinct <c>reason</c> the events of the window declare,
    /// per action. Refusals are recorded as evidence with a reason in their
    /// details, and counting them is a query: the details column is the
    /// queryable surface of the trail, and the proof of any single one of them
    /// is the canonical text, not this count.
    /// </summary>
    public required IReadOnlyList<AuditActionReasonCount> ReasonCounts { get; init; }

    /// <summary>
    /// Every governed change of the window, rebuilt from canonical text and
    /// ordered by chain sequence.
    /// </summary>
    public required IReadOnlyList<AuditGovernedChange> GovernedChanges { get; init; }

    /// <summary>Approvals granted inside the window, oldest first.</summary>
    public required IReadOnlyList<ApprovalRecord> Approvals { get; init; }

    /// <summary>Outcome of the chain verification rounds recorded inside the window, by partition.</summary>
    public required IReadOnlyList<AuditChainVerificationOutcome> ChainVerifications { get; init; }

    /// <summary>
    /// Rows of the window that carry no chain at all. They predate the chain
    /// and nothing was fabricated for them; the count keeps their absence
    /// visible instead of turning it into a silent hole.
    /// </summary>
    public required long UnchainedRows { get; init; }
}

/// <summary>How many times one action was recorded inside the window.</summary>
public sealed record AuditActionCount
{
    public required string Action { get; init; }

    public required long Count { get; init; }
}

/// <summary>How many times one action declared one reason inside the window.</summary>
public sealed record AuditActionReasonCount
{
    public required string Action { get; init; }

    public required string Reason { get; init; }

    public required long Count { get; init; }
}

/// <summary>
/// One governed change, as the canonical text records it. The actor type
/// travels with the actor: a switch this platform threw by itself and a switch
/// a person threw are the same action, and only the actor type tells them
/// apart.
/// </summary>
public sealed record AuditGovernedChange
{
    public required long Seq { get; init; }

    public required string Action { get; init; }

    public required string EntityType { get; init; }

    public required string EntityId { get; init; }

    public required string ActorType { get; init; }

    public required string ActorId { get; init; }

    public string? Application { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>Lowercase hex of the chain link that covers this event.</summary>
    public required string Hash { get; init; }
}

/// <summary>
/// Verification rounds recorded for one partition inside the window. Rounds
/// repeat on a cadence, so what a periodic review reads is how many held, how
/// many did not, and when each side last happened.
/// </summary>
public sealed record AuditChainVerificationOutcome
{
    /// <summary>Partition the rounds covered, named as the store names it.</summary>
    public required string Partition { get; init; }

    public required long IntactRounds { get; init; }

    public required long FailedRounds { get; init; }

    public DateTimeOffset? LastIntactAt { get; init; }

    public DateTimeOffset? LastFailureAt { get; init; }
}
