using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline;

/// <summary>One recorded stage decision; the trace becomes audit evidence at commit.</summary>
public sealed record StageTraceEntry(string Stage, StageOutcome Outcome, string? Reason);

/// <summary>Records the trace of one pipeline run, stage by stage.</summary>
public sealed class StageTrace
{
    private readonly List<StageTraceEntry> _entries = [];

    public IReadOnlyList<StageTraceEntry> Entries => _entries;

    public void Add(string stage, StageOutcome outcome, string? reason)
        => _entries.Add(new StageTraceEntry(stage, outcome, reason));
}

/// <summary>How the run ends in the database; derived from the stage outcomes.</summary>
public enum PipelineResultKind
{
    Dispatched,
    Rejected,
    Expired,
    Deferred,
}

/// <summary>Outcome of the single-transaction commit of one pipeline run.</summary>
public abstract record PipelineCommitResult
{
    private PipelineCommitResult()
    {
    }

    /// <summary>Everything committed together.</summary>
    public sealed record Committed(PipelineResultKind Kind) : PipelineCommitResult;

    /// <summary>
    /// The dedupe mark already existed: a redelivery after a successful
    /// commit. Nothing was written.
    /// </summary>
    public sealed record Duplicate : PipelineCommitResult;
}

/// <summary>
/// Mutable context one notification carries through the ordered stage list.
/// Each stage fills its slice with explicit nulls until then; the commit at
/// the end writes everything in one database transaction through the
/// committer the processor injected.
/// </summary>
public sealed class NotificationContext(
    Notification notification,
    Guid envelopeMessageId,
    IPipelineCommitter committer)
{
    private readonly HashSet<string> _remainingChannels = new(StringComparer.Ordinal);

    public Notification Notification { get; } = notification;

    /// <summary>Envelope message id of the queue message that carried this run; joins the dedupe mark.</summary>
    public Guid EnvelopeMessageId { get; } = envelopeMessageId;

    public StageTrace Trace { get; } = new();

    /// <summary>Reason of the last stage decision, filled before a stage returns Reject or Defer.</summary>
    public string? LastReason { get; set; }

    /// <summary>Set by the Validate stage when the TTL already expired; the commit writes expired, not rejected.</summary>
    public bool Expired { get; private set; }

    /// <summary>Release instant of a deferral, filled before a stage returns Defer.</summary>
    public DateTimeOffset? DeferReleaseAt { get; set; }

    /// <summary>Decision metadata of the published template; filled by Validate.</summary>
    public PublishedTemplate? Template { get; set; }

    /// <summary>Decrypted variables object; filled by Validate, null when the request carried none.</summary>
    public JsonElement? Variables { get; set; }

    /// <summary>Recipient snapshot; filled by Resolve. PII exists from here on, in memory only.</summary>
    public RecipientSnapshot? Recipient { get; set; }

    /// <summary>Published class policy; filled by Policy before the rules run.</summary>
    public PublishedClassPolicy? Policy { get; set; }

    /// <summary>Rule-by-rule decisions recorded by the Policy stage; committed as policy_evaluation rows.</summary>
    public List<PolicyEvaluation> PolicyEvaluations { get; } = [];

    /// <summary>Channels still eligible after the rules that already ran.</summary>
    public IReadOnlySet<string> RemainingChannels => _remainingChannels;

    /// <summary>Delivery plan restricted to the surviving channels; filled by the channel selection rule.</summary>
    public IReadOnlyList<DeliveryPlanStep>? DeliveryPlan { get; set; }

    /// <summary>Render of the first plan step's channel; filled by Render.</summary>
    public PublishedTemplateRender? Render { get; set; }

    /// <summary>Envelope-encrypted rendered content for the attempt row; filled by Render.</summary>
    public byte[]? RenderedContentEncrypted { get; set; }

    /// <summary>Contact point the first attempt targets; filled by Route, null for push.</summary>
    public Guid? SelectedContactPointId { get; set; }

    /// <summary>Wait before the fallback step; filled by Route from the first plan step.</summary>
    public TimeSpan? FallbackTimeout { get; set; }

    /// <summary>Dispatch queue destination of the first attempt; filled by Route.</summary>
    public string? DispatchDestination { get; set; }

    public void MarkExpired()
    {
        Expired = true;
        LastReason = "expired";
    }

    public void InitializeRemainingChannels(IEnumerable<string> channels)
    {
        _remainingChannels.Clear();
        _remainingChannels.UnionWith(channels);
    }

    public void RestrictRemainingChannels(IEnumerable<string> channels)
        => _remainingChannels.IntersectWith(channels);

    /// <summary>Commits the whole run in one database transaction.</summary>
    public Task<PipelineCommitResult> CommitAsync(CancellationToken cancellationToken)
        => committer.CommitAsync(this, cancellationToken);
}

/// <summary>
/// Writes one pipeline result in a single database transaction: the
/// notification transition, the first attempt, the policy evaluations, the
/// outbox message, the audit event and the dedupe mark commit together or not
/// at all.
/// </summary>
public interface IPipelineCommitter
{
    Task<PipelineCommitResult> CommitAsync(NotificationContext context, CancellationToken cancellationToken);
}
