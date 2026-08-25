using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;

/// <summary>
/// The provider's delivery dialect, read in one place by both halves of
/// feedback: the callback this provider pushes and the message resource this
/// module pulls when no callback ever came. The two speak the same words, and
/// one message reaches the same canonical meaning and the same event identity
/// whichever half observed it.
/// <para>
/// Sharing the identity is what keeps a refusal from being counted twice. The
/// hub deduplicates provider events by <c>(provider, provider event id)</c>,
/// and the consequence of a duplicate is not academic on this channel: the
/// contact ledger closes an SMS destination on the second refusal inside a
/// week, so one refusal seen by both halves under two identities would take a
/// reachable number away from a person who was refused once.
/// </para>
/// </summary>
internal static class TwilioDeliveryVocabulary
{
    /// <summary>
    /// The canonical meaning of one provider status word, or null when the
    /// word is outside the mapped vocabulary. Null is never guessed into a
    /// state: each half of feedback decides what to do about a provider that
    /// changed its words, and neither invents a transition.
    /// </summary>
    internal static DeliveryFeedbackKind? Kind(string status) => status switch
    {
        "queued" or "sending" or "sent" or "accepted" => DeliveryFeedbackKind.Sent,
        "delivered" => DeliveryFeedbackKind.Delivered,
        "read" => DeliveryFeedbackKind.Read,
        "failed" or "canceled" => DeliveryFeedbackKind.Failed,
        "undelivered" => DeliveryFeedbackKind.Bounced,
        _ => null,
    };

    /// <summary>
    /// Identity of one event inside this provider. The provider mints none, so
    /// the identity is the message plus the status it reached: each status is
    /// reported once per message, which keeps the pair stable across every
    /// redelivery of a callback and across every later reading of the same
    /// message resource.
    /// </summary>
    internal static string EventId(string messageSid, string status) => $"{messageSid}:{status}";

    /// <summary>The provider's own spelling, normalized the same way on both halves.</summary>
    internal static string Normalize(string status) => status.Trim().ToLowerInvariant();
}
