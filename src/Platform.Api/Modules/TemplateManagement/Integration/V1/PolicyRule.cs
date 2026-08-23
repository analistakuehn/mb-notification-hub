namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// One rule of the Policy stage. This module owns the contract together with
/// the definition it reads, so rules and governance can never disagree about
/// the vocabulary; the notification pipeline that executes the ordered rule
/// list lives outside this module and closes <typeparamref name="TContext"/>
/// with its own per-notification context. Each rule receives the remaining
/// channel set through that context, reads its slice of the published
/// definition, and reports a <see cref="PolicyRuleResult"/> that the pipeline
/// composes: filters intersect, and the first defer or reject stops the run.
/// </summary>
public interface IPolicyRule<in TContext>
{
    /// <summary>Stable rule name recorded with every evaluation.</summary>
    string Name { get; }

    Task<PolicyRuleResult> EvaluateAsync(
        TContext context,
        ClassPolicyDefinition policy,
        CancellationToken cancellationToken);
}

/// <summary>
/// Outcome of one policy rule over the remaining channel set. Every result
/// carries compact JSON evidence, because the rule-by-rule decision is an
/// audit record: the trail must answer "why this channel" without re-running
/// anything.
/// </summary>
public abstract record PolicyRuleResult
{
    private PolicyRuleResult()
    {
    }

    /// <summary>Compact JSON document with the rule-specific evidence. Never personal data.</summary>
    public required string EvidenceJson { get; init; }

    /// <summary>The rule lets every remaining channel through.</summary>
    public sealed record Allow : PolicyRuleResult;

    /// <summary>The rule restricts delivery to the intersection with this channel set.</summary>
    public sealed record FilterChannels(IReadOnlySet<Channel> Channels) : PolicyRuleResult;

    /// <summary>The rule postpones the notification until the release instant.</summary>
    public sealed record Defer(DateTimeOffset ReleaseAt) : PolicyRuleResult;

    /// <summary>The rule rejects the notification with a stable reason.</summary>
    public sealed record Reject(string Reason) : PolicyRuleResult;
}
