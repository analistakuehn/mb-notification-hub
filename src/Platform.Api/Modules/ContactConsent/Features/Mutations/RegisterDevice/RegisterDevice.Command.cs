namespace NotificationHub.Api.Modules.ContactConsent.Features.Mutations;

internal static partial class RegisterDevice
{
    /// <summary>
    /// One push token registration from the recipient's app. Re-posting the
    /// same token refreshes the last-seen instant and the app version instead
    /// of duplicating the registration.
    /// </summary>
    internal sealed record Command(string Token, string Platform)
    {
        public string? AppVersion { get; init; }
    }
}
