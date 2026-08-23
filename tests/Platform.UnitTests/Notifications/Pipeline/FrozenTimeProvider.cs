namespace NotificationHub.UnitTests.Notifications.Pipeline;

/// <summary>A clock frozen at one instant, so time-driven rules assert exact values.</summary>
internal sealed class FrozenTimeProvider(DateTimeOffset instant) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => instant;
}
