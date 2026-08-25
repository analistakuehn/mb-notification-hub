using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;

/// <summary>
/// The part of the attempt state machine that delivery feedback drives, as a
/// pure function of the stored status and the canonical feedback. It is
/// written apart from anything that touches a database so the table of
/// transitions can be read as a table, and so the same table answers feedback
/// pushed by a callback and feedback pulled by a later reconciliation.
/// <para>
/// The table is deliberately closed. Feedback that names no transition out of
/// the current status is not an error and not a defect: providers resend, they
/// reorder, and they report progress this hub already knows about. What such
/// feedback must never do is walk an attempt backwards, so it produces no
/// transition at all.
/// </para>
/// <para>
/// The parked state is the exception that proves the rule. An inconclusive
/// verdict is this hub admitting it does not know what the provider did, not a
/// fact about the message, so every canonical answer moves it: nothing can
/// walk backwards from a state that asserts nothing.
/// </para>
/// </summary>
internal static class DeliveryStateMachine
{
    /// <summary>
    /// The status this feedback moves the attempt to, or null when it moves
    /// nothing from where the attempt currently stands.
    /// </summary>
    internal static string? NextStatus(string currentStatus, DeliveryFeedbackKind kind)
        => (currentStatus, kind) switch
        {
            // Acceptance by the provider is not delivery; the confirmation is
            // what closes the send.
            (NotificationAttemptStatuses.Sent, DeliveryFeedbackKind.Delivered)
                => NotificationAttemptStatuses.Delivered,

            // Reading implies delivery, so it is only ever read from a
            // confirmed delivery: an open reported before the confirmation
            // would otherwise erase the confirmation that is still coming.
            (NotificationAttemptStatuses.Delivered, DeliveryFeedbackKind.Read)
                => NotificationAttemptStatuses.Read,

            // The provider answered the send after this hub stopped waiting:
            // the attempt is still owned by the claim, and the answer settles
            // it exactly as a synchronous verdict would have.
            (NotificationAttemptStatuses.Sending, DeliveryFeedbackKind.Failed)
                => NotificationAttemptStatuses.Failed,
            (NotificationAttemptStatuses.Sending, DeliveryFeedbackKind.Bounced)
                => NotificationAttemptStatuses.Bounced,

            // The destination rejected a message the provider had accepted,
            // which is the ordinary shape of a bounce.
            (NotificationAttemptStatuses.Sent, DeliveryFeedbackKind.Bounced)
                => NotificationAttemptStatuses.Bounced,

            // The message the provider accepted will not arrive for a reason
            // that does not accuse the destination: a block, a cancellation, a
            // drop. It ends the attempt exactly like a bounce does, and only
            // the accusation differs, which is what the suppression signal
            // carries and the status deliberately does not.
            (NotificationAttemptStatuses.Sent, DeliveryFeedbackKind.Failed)
                => NotificationAttemptStatuses.Failed,

            // Out of the parked state, which is the whole reason a later
            // reconciliation exists. An inconclusive verdict means this hub
            // does not know whether the provider took the message, so every
            // canonical answer is news: the provider taking it is progress,
            // and any conclusion about it settles the attempt.
            (NotificationAttemptStatuses.Unknown, DeliveryFeedbackKind.Sent)
                => NotificationAttemptStatuses.Sent,
            (NotificationAttemptStatuses.Unknown, DeliveryFeedbackKind.Delivered)
                => NotificationAttemptStatuses.Delivered,
            (NotificationAttemptStatuses.Unknown, DeliveryFeedbackKind.Read)
                => NotificationAttemptStatuses.Read,
            (NotificationAttemptStatuses.Unknown, DeliveryFeedbackKind.Failed)
                => NotificationAttemptStatuses.Failed,
            (NotificationAttemptStatuses.Unknown, DeliveryFeedbackKind.Bounced)
                => NotificationAttemptStatuses.Bounced,

            _ => null,
        };
}
