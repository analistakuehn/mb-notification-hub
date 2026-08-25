namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Authorization;

/// <summary>
/// Named authorization policy of the contact and consent write surface: every
/// route only admits principals carrying the dedicated write role, whether the
/// registration system (client credentials) or an internal operations tool.
/// </summary>
public static class ContactConsentAuthorizationSetup
{
    public const string WritePolicyName = "contacts-write";

    public const string WriteRole = "Contacts.Write";

    /// <summary>
    /// Reversing a suppression is not an ordinary contact write: it re-opens a
    /// channel an automatic decision closed, so it carries a role of its own
    /// and the registration system that feeds this module never holds it.
    /// </summary>
    public const string SuppressionRemovalPolicyName = "contacts-suppression-removal";

    public const string SuppressionRemovalRole = "Contacts.Suppression.Manage";

    public static IServiceCollection AddContactConsentAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(WritePolicyName, policy => policy.RequireRole(WriteRole))
            .AddPolicy(SuppressionRemovalPolicyName, policy => policy.RequireRole(SuppressionRemovalRole));
        return services;
    }
}
