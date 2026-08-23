namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Lifecycle states of a notification. Ingestion only ever writes the first
/// one; the pipeline commit writes the others. There is no persisted
/// intermediate processing state: the queue visibility timeout is the claim,
/// and one commit lands directly on the resulting state.
/// </summary>
public static class NotificationStatuses
{
    public const string Accepted = "accepted";
    public const string Dispatched = "dispatched";
    public const string Rejected = "rejected";
    public const string Expired = "expired";
    public const string Deferred = "deferred";

    /// <summary>
    /// Delivery succeeded. For push this is stamped when the first sibling
    /// attempt reaches sent, because acceptance by the push provider is the
    /// delivery signal the design defines for that channel.
    /// </summary>
    public const string Delivered = "delivered";

    /// <summary>The delivery plan is exhausted: every attempt of the last step failed.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// One accepted notification request. Ingestion persists the request exactly
/// as governed data: the masked variables projection for queries and audit,
/// the encrypted full variables for the render stage, and the identity of the
/// producer that asked. Pipeline stages own every later state transition.
/// </summary>
public sealed class Notification
{
    private Notification()
    {
        Application = null!;
        IdempotencyKey = null!;
        RecipientId = null!;
        Class = null!;
        TemplateKey = null!;
        VariablesMaskedJson = null!;
        RequestedBy = null!;
        Status = null!;
    }

    public Guid Id { get; private set; }

    public string Application { get; private set; }

    public string IdempotencyKey { get; private set; }

    public string RecipientId { get; private set; }

    public string Class { get; private set; }

    public string TemplateKey { get; private set; }

    /// <summary>Published version the ingestion validated against; the render stage re-reads it.</summary>
    public int TemplateVersion { get; private set; }

    /// <summary>Stamped by the Policy stage of the pipeline; null until then.</summary>
    public int? PolicyVersion { get; private set; }

    /// <summary>Variables with every sensitive value masked; the only plaintext projection ever stored.</summary>
    public string VariablesMaskedJson { get; private set; }

    /// <summary>Envelope-encrypted full variables object; null when the request carried none.</summary>
    public byte[]? VariablesEncrypted { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>Stable identity (appid/oid) of the producer token that requested the notification.</summary>
    public string RequestedBy { get; private set; }

    public string Status { get; private set; }

    /// <summary>Scheduling instant when the producer asked for a deferred release.</summary>
    public DateTimeOffset? ReleaseAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>
    /// Stamps the policy version that ruled the notification and records the
    /// dispatch: the first attempt is queued and the pipeline is done with
    /// this notification until delivery feedback arrives.
    /// </summary>
    public void MarkDispatched(int policyVersion)
    {
        EnsurePipelineCanTransition();
        PolicyVersion = policyVersion;
        Status = NotificationStatuses.Dispatched;
    }

    /// <summary>
    /// Records a policy or validation rejection: a valid business outcome,
    /// never an error. The encrypted variables are purged because the render
    /// stage will never need them again.
    /// </summary>
    public void MarkRejected(int? policyVersion)
    {
        EnsurePipelineCanTransition();
        PolicyVersion = policyVersion ?? PolicyVersion;
        Status = NotificationStatuses.Rejected;
        VariablesEncrypted = null;
    }

    /// <summary>Records TTL expiry before any dispatch; purges the encrypted variables.</summary>
    public void MarkExpired()
    {
        EnsurePipelineCanTransition();
        Status = NotificationStatuses.Expired;
        VariablesEncrypted = null;
    }

    /// <summary>
    /// Parks the notification until the release instant. The variables stay
    /// encrypted because the pipeline resumes from here after the release.
    /// </summary>
    public void MarkDeferred(DateTimeOffset releaseAt, int policyVersion)
    {
        EnsurePipelineCanTransition();
        PolicyVersion = policyVersion;
        ReleaseAt = releaseAt;
        Status = NotificationStatuses.Deferred;
    }

    /// <summary>
    /// Re-stamps the template version when the published version moved
    /// between ingestion and render: the notification always records exactly
    /// the version whose content it rendered.
    /// </summary>
    public void RestampTemplateVersion(int renderedVersion)
    {
        EnsurePipelineCanTransition();
        TemplateVersion = renderedVersion;
    }

    /// <summary>
    /// Records the delivery success: for push, the first sibling attempt the
    /// provider accepted. Only the first success transitions; later sibling
    /// verdicts leave the state untouched.
    /// </summary>
    public void MarkDelivered()
    {
        EnsureDispatchCanTransition();
        Status = NotificationStatuses.Delivered;
    }

    /// <summary>
    /// Records the exhaustion of the delivery plan: the failing attempt was
    /// the last step and, for push, every sibling failed.
    /// </summary>
    public void MarkFailedAfterDispatch()
    {
        EnsureDispatchCanTransition();
        Status = NotificationStatuses.Failed;
    }

    /// <summary>
    /// Records TTL expiry found while handling a fallback trigger: the plan
    /// still had steps, but the notification's validity ended first. The
    /// encrypted variables are purged because no render will ever run again.
    /// </summary>
    public void MarkExpiredAfterDispatch()
    {
        EnsureDispatchCanTransition();
        Status = NotificationStatuses.Expired;
        VariablesEncrypted = null;
    }

    private void EnsurePipelineCanTransition()
    {
        if (Status != NotificationStatuses.Accepted)
        {
            throw new InvalidOperationException(
                $"A notificação {Id} está em '{Status}' e não aceita transição do pipeline.");
        }
    }

    private void EnsureDispatchCanTransition()
    {
        if (Status != NotificationStatuses.Dispatched)
        {
            throw new InvalidOperationException(
                $"A notificação {Id} está em '{Status}' e não aceita transição do despacho.");
        }
    }

    public static Notification Accept(NotificationDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Application);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.IdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.RecipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.TemplateKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.VariablesMaskedJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.RequestedBy);
        if (!NotificationClasses.IsCanonical(draft.Class))
        {
            throw new ArgumentException($"Classe de notificação desconhecida: '{draft.Class}'.", nameof(draft));
        }

        if (draft.TtlSeconds <= 0)
        {
            throw new ArgumentException("O TTL da notificação deve ser positivo.", nameof(draft));
        }

        return new Notification
        {
            Id = Guid.CreateVersion7(),
            Application = draft.Application,
            IdempotencyKey = draft.IdempotencyKey,
            RecipientId = draft.RecipientId,
            Class = draft.Class,
            TemplateKey = draft.TemplateKey,
            TemplateVersion = draft.TemplateVersion,
            PolicyVersion = null,
            VariablesMaskedJson = draft.VariablesMaskedJson,
            VariablesEncrypted = draft.VariablesEncrypted,
            CorrelationId = draft.CorrelationId,
            RequestedBy = draft.RequestedBy,
            Status = NotificationStatuses.Accepted,
            ReleaseAt = draft.ScheduledAt,
            ExpiresAt = draft.AcceptedAt.AddSeconds(draft.TtlSeconds),
            CreatedAt = draft.AcceptedAt,
        };
    }
}

/// <summary>Validated inputs of an acceptance, gathered by the ingestion use case.</summary>
public sealed record NotificationDraft
{
    public required string Application { get; init; }

    public required string IdempotencyKey { get; init; }

    public required string RecipientId { get; init; }

    public required string Class { get; init; }

    public required string TemplateKey { get; init; }

    public required int TemplateVersion { get; init; }

    public required string VariablesMaskedJson { get; init; }

    public byte[]? VariablesEncrypted { get; init; }

    public string? CorrelationId { get; init; }

    public required string RequestedBy { get; init; }

    public required int TtlSeconds { get; init; }

    public DateTimeOffset? ScheduledAt { get; init; }

    public required DateTimeOffset AcceptedAt { get; init; }
}
