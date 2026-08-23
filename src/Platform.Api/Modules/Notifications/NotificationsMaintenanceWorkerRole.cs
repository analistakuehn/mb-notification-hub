using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

namespace NotificationHub.Api.Modules.Notifications;

/// <summary>
/// Composition of the <c>notifications-maintenance</c> worker role, owned by
/// this module: the rear-guard sweep that discards the complete rendered
/// content of attempts no verdict ever reached, and the gated backfill over
/// the content sealed before the two-form envelope existed.
/// </summary>
/// <remarks>
/// A role of its own, and not a passenger of the core or dispatcher roles, for
/// two reasons. Both jobs rewrite stored ciphertext of governed rows, which is
/// not work that may run once per replica of a hot path scaled by queue depth.
/// And a retention rule must not depend on traffic to run: the core and
/// dispatcher roles scale down when their queues drain, which is exactly when
/// an abandoned attempt sits unread with the complete form still in it. The
/// data stays owned by this module, so the write stays here too.
/// </remarks>
public sealed class NotificationsMaintenanceWorkerRole : IWorkerRoleModule
{
    public static string Role => "notifications-maintenance";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddEnvelopeEncryption(configuration);

        // The backfill rebuilds the masked form from the published template,
        // so the role reads TemplateManagement through its published contract,
        // exactly like the pipeline does.
        services.AddTemplateManagementReadSurface(configuration);
        services.AddNotificationsPersistence(configuration);

        services.AddOptions<RenderedContentRetentionOptions>()
            .Bind(configuration.GetSection(RenderedContentRetentionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<RenderedContentSweep>();
        services.AddHostedService<RenderedContentSweepService>();

        services.AddOptions<RenderedContentBackfillOptions>()
            .Bind(configuration.GetSection(RenderedContentBackfillOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<RenderedContentBackfill>();
        services.AddHostedService<RenderedContentBackfillService>();
    }
}
