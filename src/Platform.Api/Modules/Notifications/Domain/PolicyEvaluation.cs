namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>Canonical result values one policy rule can record.</summary>
public static class PolicyEvaluationResults
{
    public const string Allow = "allow";
    public const string FilterChannels = "filter-channels";
    public const string Defer = "defer";
    public const string Reject = "reject";
}

/// <summary>
/// The recorded decision of one policy rule over one notification: which rule
/// ran, what it decided, why, and the compact JSON evidence that lets the
/// trail answer "why this channel" without re-running anything.
/// </summary>
public sealed class PolicyEvaluation
{
    private PolicyEvaluation()
    {
        Rule = null!;
        Result = null!;
        EvidenceJson = null!;
    }

    public Guid Id { get; private set; }

    public Guid NotificationId { get; private set; }

    public string Rule { get; private set; }

    public string Result { get; private set; }

    /// <summary>Stable reason on defer and reject; null on allow and filter.</summary>
    public string? Reason { get; private set; }

    /// <summary>Compact JSON evidence of the rule decision. Never personal data.</summary>
    public string EvidenceJson { get; private set; }

    public DateTimeOffset EvaluatedAt { get; private set; }

    public static PolicyEvaluation Record(
        Guid notificationId,
        string rule,
        string result,
        string? reason,
        string evidenceJson,
        DateTimeOffset evaluatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(result);
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceJson);

        return new PolicyEvaluation
        {
            Id = Guid.CreateVersion7(),
            NotificationId = notificationId,
            Rule = rule,
            Result = result,
            Reason = reason,
            EvidenceJson = evidenceJson,
            EvaluatedAt = evaluatedAt,
        };
    }
}
