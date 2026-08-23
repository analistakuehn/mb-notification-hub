namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;

/// <summary>
/// Audit vocabulary of the Core pipeline, composed in this module's naming as
/// the audit contract expects from producing contexts. The actor is the hub
/// itself: the pipeline decides from governed data, never from a session.
/// </summary>
internal static class PipelineAuditVocabulary
{
    internal const string ActorTypeSystem = "system";
    internal const string ActorIdCoreWorker = "core-worker";

    internal const string EntityTypeNotification = "notification";
    internal const string EntityTypeMessage = "message";

    internal const string NotificationDispatched = "notification.dispatched";
    internal const string NotificationRejected = "notification.rejected";
    internal const string NotificationDeferred = "notification.deferred";
    internal const string NotificationExpired = "notification.expired";
    internal const string NotificationDuplicate = "notification.duplicate";
    internal const string MessageDiscarded = "message.discarded";
}
