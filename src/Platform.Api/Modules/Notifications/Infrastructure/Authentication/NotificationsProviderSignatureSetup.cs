namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Authentication;

/// <summary>
/// Registers the provider-signature scheme and the named policy that admits
/// the webhook route. The policy names the scheme explicitly, so the route is
/// authenticated by the provider's signature and never by the bearer token
/// that authenticates every other route of this host: the two identities have
/// nothing in common and one must never satisfy the other's gate.
/// </summary>
public static class NotificationsProviderSignatureSetup
{
    /// <summary>Named policy of the provider webhook route.</summary>
    public const string WebhookPolicyName = "notifications-provider-webhook";

    public static IServiceCollection AddNotificationsProviderSignature(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddAuthentication()
            .AddScheme<ProviderSignatureOptions, ProviderSignatureAuthenticationHandler>(
                ProviderSignatureDefaults.SchemeName,
                options => configuration
                    .GetSection(ProviderSignatureDefaults.SectionName)
                    .Bind(options));

        services.AddAuthorizationBuilder()
            .AddPolicy(WebhookPolicyName, policy => policy
                .AddAuthenticationSchemes(ProviderSignatureDefaults.SchemeName)
                .RequireAuthenticatedUser()
                .RequireClaim(ProviderSignatureDefaults.ProviderKeyClaimType));

        return services;
    }
}
