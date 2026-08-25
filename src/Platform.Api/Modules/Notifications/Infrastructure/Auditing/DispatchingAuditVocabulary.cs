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

    /// <summary>
    /// The next plan step was queued while the previous one was still
    /// inconclusive, which is the one place this design knowingly risks a
    /// second message to the same person.
    /// <para>
    /// It exists because the risk is otherwise invisible. Every other trigger
    /// follows a verdict that rules delivery out; this one follows a send
    /// nobody can rule in or out, and the decision to go anyway is deliberate:
    /// on a critical or an authentication flow a lost code costs more than a
    /// duplicate. The duplicate itself is never observed, because a provider
    /// that never answered is a provider that never will, so the trail records
    /// the risk taken and never claims a duplicate was detected. Anything that
    /// reasons about duplicates has to read this and not the entry that names
    /// a redelivered internal message.
    /// </para>
    /// </summary>
    internal const string FallbackRequestedFromUnknown = "fallback.requested_from_unknown";
}
