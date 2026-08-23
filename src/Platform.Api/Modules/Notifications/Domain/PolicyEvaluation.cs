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

    /// <summary>
    /// Compact JSON evidence of the rule decision, in each rule's own shape.
    /// It carries no contact value, no device token and no rendered content,
    /// and it does carry facts about the recipient: the quiet-hours rule
    /// records the recipient's timezone and local time, which infer
    /// approximate geography, and the channel rule records which channels were
    /// reachable, which states whether the recipient has a usable contact.
    /// Treat this column as personal data with a narrow shape, not as a
    /// PII-free field: exposing it outside the audit surface is a privacy
    /// decision, and any projection of it belongs to an explicit allow-list per
    /// rule rather than to the raw document.
    /// </summary>
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
