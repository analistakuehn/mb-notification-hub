namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// Address of this hub on the corporate bus: the single outgoing topic every
/// module publishes its integration events to, and the URN that identifies the
/// hub as the emitting system.
///
/// The pair belongs to the platform, next to the CloudEvents envelope, because
/// it is transport contract rather than domain vocabulary. The same topic
/// carries the lifecycle events of a notification and the consent changes of
/// the contact context, so neither module can own the constant the other one
/// needs.
/// </summary>
public static class OutgoingEventBus
{
    /// <summary>Outgoing topic of the corporate bus.</summary>
    public const string Topic = "notifications.events.v1";

    /// <summary>URN of this hub as the emitting system.</summary>
    public const string Source = "urn:araia:notification-hub";
}
