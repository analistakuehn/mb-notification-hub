namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;

/// <summary>
/// Audit vocabulary of the scheduler, following the platform dot vocabulary.
/// The constants stay module-local like every other vocabulary of this
/// context, pending the same promotion decision.
/// </summary>
/// <remarks>
/// Only the release writes a trail. Asking for a fallback step does not: the
/// ask is a queue row, the decision belongs to the handler that claims the
/// step, and the handler already records both the trigger and the queued
/// attempt. A trail entry per ask would take the chain lock of the trail's
/// partition once per round, which is the same reason the callback path keeps
/// its append out of the receiving transaction. The release is different in
/// kind: it changes a notification's state, and a state change without a trail
/// is a state change nobody can reconstruct.
/// </remarks>
internal static class SchedulerAuditVocabulary
{
    internal const string ActorTypeSystem = "system";
    internal const string ActorIdDeliveryTracker = "delivery-tracker";

    internal const string EntityTypeNotification = "notification";

    /// <summary>A parked notification reached its release instant and went back to the pipeline.</summary>
    internal const string NotificationReleased = "notification.released";
}
