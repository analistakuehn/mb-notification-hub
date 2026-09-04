using Microsoft.Extensions.DependencyInjection.Extensions;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capability;

/// <summary>
/// Binds the deployment state of the capability. There is no startup guard
/// here and the absence of one is the decision: every other section of this
/// module refuses a value nobody set, because an unset ceiling or an unset
/// retention window would be a product choice made by omission. This section
/// is the one whose omission is a legitimate state, and it is the state the
/// capability is deployed in.
/// </summary>
internal static class AttachmentCapabilitySetup
{
    internal static IServiceCollection AddAttachmentCapability(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttachmentCapabilityOptions>()
            .Bind(configuration.GetSection(AttachmentCapabilityOptions.SectionName));
        services.TryAddSingleton<AttachmentCapability>();
        return services;
    }
}
