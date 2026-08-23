namespace NotificationHub.Api.Modules.Notifications.Domain;

/// <summary>
/// One idempotency registration of the ingestion, scoped to
/// (application, idempotency key). The table is the authority of the
/// idempotency contract: it stays outside the monthly partitioning exactly so
/// its unique key can exist, and a purge job removes registrations after the
/// contract window, which is why a replay beyond it creates a new
/// notification.
/// </summary>
public sealed class IdempotencyRegistration
{
    private IdempotencyRegistration()
    {
        Application = null!;
        IdempotencyKey = null!;
        PayloadHash = null!;
    }

    public string Application { get; private set; }

    public string IdempotencyKey { get; private set; }

    /// <summary>SHA-256 (lowercase hex) of the canonical request body; a replay must match it.</summary>
    public string PayloadHash { get; private set; }

    public Guid NotificationId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static IdempotencyRegistration Register(
        string application,
        string idempotencyKey,
        string payloadHash,
        Guid notificationId,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(application);
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadHash);
        return new IdempotencyRegistration
        {
            Application = application,
            IdempotencyKey = idempotencyKey,
            PayloadHash = payloadHash,
            NotificationId = notificationId,
            CreatedAt = createdAt,
        };
    }
}
