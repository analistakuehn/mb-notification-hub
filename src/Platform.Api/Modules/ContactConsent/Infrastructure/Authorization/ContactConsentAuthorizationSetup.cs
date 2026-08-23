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

    public static IServiceCollection AddContactConsentAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder()
            .AddPolicy(WritePolicyName, policy => policy.RequireRole(WriteRole));
        return services;
    }
}
