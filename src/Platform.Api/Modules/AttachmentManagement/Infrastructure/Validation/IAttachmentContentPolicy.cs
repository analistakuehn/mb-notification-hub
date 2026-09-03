namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Validation;

/// <summary>What a policy decided about one attachment's content.</summary>
internal enum AttachmentPolicyDecision
{
    /// <summary>
    /// The content may be released. This is the only value that opens the
    /// gate, and the machine treats every other value, and the absence of a
    /// verdict, as a closed one.
    /// </summary>
    Approved,

    /// <summary>The content is refused, and the refusal is final.</summary>
    Refused,

    /// <summary>
    /// The policy could not conclude. It is not a refusal and it is not an
    /// approval: the attachment waits, and only its deadline ends the wait.
    /// </summary>
    Inconclusive,
}

/// <summary>
/// What a policy is told about the content, and nothing else. There is no
/// storage coordinate here on purpose: a policy that could name the object
/// could hand that name to whatever it talks to, and the coordinate is the one
/// thing this module never publishes.
/// </summary>
internal sealed record AttachmentContentSubject(
    string DeclaredContentType,
    string? DetectedContentType,
    long SizeBytes);

/// <summary>
/// One decision, with the fine detail that names it. The detail is durable
/// state and never a public answer, so a policy is free to be specific in it.
/// </summary>
internal sealed record AttachmentPolicyVerdict
{
    private AttachmentPolicyVerdict(AttachmentPolicyDecision decision, string detail)
    {
        Decision = decision;
        Detail = detail;
    }

    internal AttachmentPolicyDecision Decision { get; }

    /// <summary>Empty for an approval: there is nothing to name about one.</summary>
    internal string Detail { get; }

    internal static AttachmentPolicyVerdict Approve()
        => new(AttachmentPolicyDecision.Approved, string.Empty);

    internal static AttachmentPolicyVerdict Refuse(string detail)
        => new(AttachmentPolicyDecision.Refused, detail);

    internal static AttachmentPolicyVerdict DidNotConclude(string detail)
        => new(AttachmentPolicyDecision.Inconclusive, detail);
}

/// <summary>
/// The seam the executable policy arrives through. The implementation this
/// module ships decides on the declared and the recognized type alone; an
/// implementation that also scans the bytes answers the same way, and nothing
/// in the machine around it moves when that one arrives.
/// </summary>
internal interface IAttachmentContentPolicy
{
    Task<AttachmentPolicyVerdict> EvaluateAsync(
        AttachmentContentSubject subject,
        CancellationToken cancellationToken);
}
