using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Reconciliation;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;

namespace NotificationHub.Api.Modules.Notifications;

/// <summary>
/// Composition of the <c>notifications-maintenance</c> worker role, owned by
/// this module: the rear-guard sweep that discards the complete rendered
/// content of attempts no verdict ever reached, the gated backfill over the
/// content sealed before the two-form envelope existed, and the daily
/// reconciliation that asks providers about the sends they never reported on.
/// </summary>
/// <remarks>
/// A role of its own, and not a passenger of the core or dispatcher roles, for
/// two reasons. Both jobs rewrite stored ciphertext of governed rows, which is
/// not work that may run once per replica of a hot path scaled by queue depth.
/// And a retention rule must not depend on traffic to run: the core and
/// dispatcher roles scale down when their queues drain, which is exactly when
/// an abandoned attempt sits unread with the complete form still in it. The
/// data stays owned by this module, so the write stays here too.
/// <para>
/// The reconciliation belongs to this role and not to the delivery tracker for
/// the same two reasons. It is a batch that reads the whole table and calls
/// providers in sequence, which is precisely the long transaction the tracker
/// role refuses to sit behind, and it must run whether or not any queue has
/// traffic: the attempts it corrects are the ones nothing else will ever come
/// back for. A singleton is also what it wants, because every round would
/// otherwise pay one provider read per replica for the same rows.
/// </para>
/// </remarks>
public sealed class NotificationsMaintenanceWorkerRole : IWorkerRoleModule
{
    public static string Role => "notifications-maintenance";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformMessaging(configuration);
        services.AddEnvelopeEncryption(configuration);

        // The backfill rebuilds the masked form from the published template,
        // so the role reads TemplateManagement through its published contract,
        // exactly like the pipeline does.
        services.AddTemplateManagementReadSurface(configuration);
        services.AddNotificationsPersistence(configuration);
        services.AddNotificationsKillSwitch();
        services.AddNotificationsKillSwitchHoldReleaser();

        // The reconciliation reads and corrects delivery state, so this role
        // composes exactly the three surfaces that correction needs and
        // nothing else: the providers it may ask, the contacts it may reveal a
        // destination from, and the trail every correction writes.
        services.AddDispatchDeliveryLookupSurface(configuration);
        services.AddContactConsentReadSurface(configuration);
        services.AddContactConsentSuppressionLedger(configuration);
        services.AddAuditTrailSurface();

        services.AddOptions<DeliveryReconciliationOptions>()
            .Bind(configuration.GetSection(DeliveryReconciliationOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<DeliveryEventWriter>();
        services.AddScoped<DeliveryStateApplier>();
        services.AddScoped<ScanIndexLiabilitySweep>();
        services.AddScoped<DeliveryReconciliationScan>();
        services.AddHostedService<DeliveryReconciliationService>();

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
