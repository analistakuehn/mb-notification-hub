namespace NotificationHub.Api.Modules.Notifications.Domain;

public static class KillSwitchWorkKinds
{
    public const string Core = "core";
    public const string Fallback = "fallback";
    public const string Dispatch = "dispatch";
}

/// <summary>
/// Durable claim check for work removed from a transient queue while an
/// emergency stop is active. It stores identifiers only, never rendered
/// content, contact values, or device tokens.
/// </summary>
public sealed class KillSwitchHold
{
    // EF Core materialization and query projection only; production writes
    // use a concurrency-safe PostgreSQL upsert.
    private KillSwitchHold()
    {
        WorkKind = null!;
        WorkId = null!;
        Scope = null!;
        Key = null!;
        Destination = null!;
        PayloadJson = null!;
    }

    public Guid Id { get; }

    public string WorkKind { get; }

    public string WorkId { get; }

    public string Scope { get; }

    public string Key { get; }

    public string Destination { get; }

    public string PayloadJson { get; }

    public DateTimeOffset ExpiresAt { get; }

    public DateTimeOffset? ReleasedAt { get; }

    public long Version { get; }
}
