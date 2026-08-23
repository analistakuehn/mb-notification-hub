namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// Attempt states the pipeline knows about. The Core only ever writes the
/// first one; the dispatch side owns every later transition through its own
/// optimistic lock over the queued state.
/// </summary>
public static class NotificationAttemptStatuses
{
    public const string Queued = "queued";
}

/// <summary>
/// One delivery attempt of a notification: the channel the plan chose, the
/// encrypted rendered content the dispatcher will send, and the fallback
/// deadline stamped at enqueue time, never at send time. The provider fields
/// stay empty until a dispatcher claims the attempt.
/// </summary>
public sealed class NotificationAttempt
{
    private NotificationAttempt()
    {
        Channel = null!;
        RenderedContentEncrypted = null!;
        ContentHashFull = null!;
        ContentHashMasked = null!;
        Status = null!;
    }

    public Guid Id { get; private set; }

    public Guid NotificationId { get; private set; }

    /// <summary>1-based position inside the notification's delivery plan.</summary>
    public int Sequence { get; private set; }

    public string Channel { get; private set; }

    /// <summary>Stamped by the dispatcher when it claims the attempt.</summary>
    public string? ProviderKey { get; private set; }

    /// <summary>Contact point the attempt targets; null for push, whose targets are device tokens.</summary>
    public Guid? ContactPointId { get; private set; }

    public string? ProviderMessageId { get; private set; }

    /// <summary>Envelope-encrypted rendered content, sealed with the application's data key.</summary>
    public byte[] RenderedContentEncrypted { get; private set; }

    /// <summary>Canonical hash of the complete rendered content, computed before any masking.</summary>
    public string ContentHashFull { get; private set; }

    /// <summary>Canonical hash of the masked render, the only form a trail may store.</summary>
    public string ContentHashMasked { get; private set; }

    public string Status { get; private set; }

    public string? ErrorCode { get; private set; }

    /// <summary>Instant after which the tracker requests the fallback step; null on the last step.</summary>
    public DateTimeOffset? FallbackDeadline { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static NotificationAttempt Queue(NotificationAttemptDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Channel);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ContentHashFull);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.ContentHashMasked);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(draft.Sequence);
        if (draft.RenderedContentEncrypted is not { Length: > 0 })
        {
            throw new ArgumentException(
                "O conteúdo renderizado cifrado é obrigatório em um attempt enfileirado.", nameof(draft));
        }

        return new NotificationAttempt
        {
            Id = Guid.CreateVersion7(),
            NotificationId = draft.NotificationId,
            Sequence = draft.Sequence,
            Channel = draft.Channel,
            ProviderKey = null,
            ContactPointId = draft.ContactPointId,
            ProviderMessageId = null,
            RenderedContentEncrypted = draft.RenderedContentEncrypted,
            ContentHashFull = draft.ContentHashFull,
            ContentHashMasked = draft.ContentHashMasked,
            Status = NotificationAttemptStatuses.Queued,
            ErrorCode = null,
            FallbackDeadline = draft.FallbackTimeout is { } timeout ? draft.QueuedAt + timeout : null,
            SentAt = null,
            DeliveredAt = null,
            CreatedAt = draft.QueuedAt,
        };
    }
}

/// <summary>Validated inputs of one queued attempt, gathered by the pipeline commit.</summary>
public sealed record NotificationAttemptDraft
{
    public required Guid NotificationId { get; init; }

    public required int Sequence { get; init; }

    public required string Channel { get; init; }

    public Guid? ContactPointId { get; init; }

    public required byte[] RenderedContentEncrypted { get; init; }

    public required string ContentHashFull { get; init; }

    public required string ContentHashMasked { get; init; }

    /// <summary>Wait before the fallback step of the plan; null when the plan has no next step.</summary>
    public TimeSpan? FallbackTimeout { get; init; }

    public required DateTimeOffset QueuedAt { get; init; }
}
