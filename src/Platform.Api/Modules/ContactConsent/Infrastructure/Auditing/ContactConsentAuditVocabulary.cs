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

    internal const string EntityTypeRecipient = "recipient";
    internal const string EntityTypeDeviceToken = "device_token";
}
