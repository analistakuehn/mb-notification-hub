namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Auditing;

/// <summary>
/// Audit vocabulary of the contact and consent writes, following the platform
/// dot vocabulary. The constants live module-locally; promoting them into the
/// Audit <c>Integration/V1</c> vocabulary is a pending cross-module decision,
/// mirroring the ingestion vocabulary of the Notifications module.
/// </summary>
internal static class ContactConsentAuditVocabulary
{
    internal const string ContactPointsDeclared = "contact.points.declared";
    internal const string ConsentsDeclared = "consents.declared";
    internal const string DeviceRegistered = "device.registered";
    internal const string DeviceInvalidated = "device.invalidated";
    internal const string MessageDiscarded = "message.discarded";

    /// <summary>One refusal recorded without reaching the channel's threshold.</summary>
    internal const string SuppressionSignalRecorded = "suppression.signal.recorded";

    /// <summary>The contact point stopped being addressable.</summary>
    internal const string SuppressionAdded = "suppression.added";

    /// <summary>An operator took the suppression back.</summary>
    internal const string SuppressionRemoved = "suppression.removed";

    internal const string EntityTypeRecipient = "recipient";
    internal const string EntityTypeDeviceToken = "device_token";
    internal const string EntityTypeMessage = "message";
    internal const string EntityTypeContactPoint = "contact_point";

    internal const string ActorTypeSystem = "system";
    internal const string ActorIdCacheWorker = "contact-consent-worker";

    /// <summary>Reporter of the provider delivery feedback that feeds the suppression ledger.</summary>
    internal const string ActorIdDeliveryFeedback = "delivery-tracker";
}
