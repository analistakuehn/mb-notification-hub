namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Reconciliation;

/// <summary>
/// Registers the repair round and the values that bound it. The round is
/// registered wherever the module is composed and the scheduler that drives it
/// is not: a job that removes durable bytes is not work that may run once per
/// replica of a request-serving host.
/// </summary>
internal static class AttachmentReconciliationSetup
{
    internal static IServiceCollection AddAttachmentReconciliation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttachmentReconciliationOptions>()
            .Bind(configuration.GetSection(AttachmentReconciliationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<AttachmentReconciliationScan>();
        return services;
    }
}
