namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;

/// <summary>
/// Audit vocabulary of the dispatch side of this module, following the
/// platform dot vocabulary. The dispatcher acts as the hub itself: every
/// decision here derives from provider verdicts and governed state, never
/// from a session. The constants live module-locally, like the ingestion and
/// pipeline vocabularies, pending the same promotion decision.
/// </summary>
internal static class DispatchingAuditVocabulary
{
    internal const string ActorTypeSystem = "system";
    internal const string ActorIdDispatcher = "dispatcher";

    internal const string EntityTypeNotification = "notification";
    internal const string EntityTypeMessage = "message";

    /// <summary>A definitive failure asked the Core for the next plan step.</summary>
    internal const string FallbackTriggered = "fallback.triggered";

    /// <summary>The provider accepted the first attempt of a channel whose acceptance is the delivery signal.</summary>
    internal const string NotificationDelivered = "notification.delivered";

    /// <summary>The delivery plan is exhausted: the last step failed with no sibling left.</summary>
    internal const string NotificationFailed = "notification.failed";

    /// <summary>The Core queued the next plan step in response to a fallback trigger.</summary>
    internal const string FallbackAttemptQueued = "fallback.attempt_queued";
}
