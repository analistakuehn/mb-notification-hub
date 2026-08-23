namespace NotificationHub.Api.Modules.ContactConsent.Domain;

/// <summary>
/// Canonical channels a contact point can address. Push is absent on purpose:
/// push routing lives in device tokens, which have their own registration
/// path and lifecycle.
/// </summary>
public static class ContactChannels
{
    public const string Email = "email";
    public const string Sms = "sms";
    public const string WhatsApp = "whatsapp";

    public static IReadOnlyList<string> CanonicalValues { get; } = [Email, Sms, WhatsApp];

    public static bool IsCanonical(string? value)
        => value is Email or Sms or WhatsApp;
}
