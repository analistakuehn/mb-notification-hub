namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>
/// Audit vocabulary of the delivery-tracking side of this module, following
/// the platform dot vocabulary. The actor is the hub itself: every entry here
/// records what a provider reported and what this hub did about it, never a
/// session. The constants stay module-local, like the ingestion, pipeline and
/// dispatch vocabularies, pending the same promotion decision.
/// </summary>
internal static class DeliveryTrackingAuditVocabulary
{
    internal const string ActorTypeSystem = "system";
    internal const string ActorIdDeliveryTracker = "delivery-tracker";

    internal const string EntityTypeNotification = "notification";

    /// <summary>
    /// One piece of provider feedback moved one attempt. A single action with
    /// the transition in its details rather than one action per transition:
    /// the reader of an evidence trail asks what the provider reported and
    /// what changed, and both answers belong to the same record.
    /// </summary>
    internal const string DeliveryEventApplied = "delivery.event_applied";
}
