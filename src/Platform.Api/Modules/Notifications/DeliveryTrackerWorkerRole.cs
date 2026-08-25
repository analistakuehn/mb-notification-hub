using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Events;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking.Scheduling;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Notifications;

/// <summary>
/// Composition of the <c>delivery-tracker</c> worker role, owned by this
/// module: the consumer of provider delivery feedback, the single applier of
/// the feedback-driven half of the attempt state machine, and the retention of
/// the deduplication ledger that feeds it.
/// </summary>
/// <remarks>
/// A role of its own, and not a passenger of the maintenance role, for three
/// reasons that all point the same way. The maintenance role rewrites stored
/// ciphertext of governed rows and runs as a singleton, while this one is safe
/// and necessary above one replica: its work is claimed per row and a stopped
/// tracker stops delivery feedback in silence. Its latency is a contract,
/// which sharing a process with a batch job would put behind a long
/// transaction. And a failure of a batch job must not take the feedback path
/// down with it.
/// </remarks>
public sealed class DeliveryTrackerWorkerRole : IWorkerRoleModule
{
    public static string Role => "delivery-tracker";

    /// <summary>
    /// Queue this role drains. The scheduler of this role adds no binding: it
    /// reads the database on a timer and writes to the outbox, so its work
    /// arrives from stored state rather than from a queue, and it stops when
    /// the process stops rather than when a queue drains.
    /// </summary>
    internal static readonly SqsQueueBinding[] Queues =
        [new SqsQueueBinding(DeliveryTrackingMessages.Destination, PriorityRank: 0)];

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformMessaging(configuration);
        services.AddSqsMessageConsuming(configuration);
        services.AddAuditTrailSurface();
        services.AddNotificationsPersistence(configuration);

        // The write side of the contact suppression ledger, behind its
        // published contract: this role observes that a provider refused a
        // destination, and the context that owns contacts decides what that
        // costs the recipient.
        services.AddContactConsentSuppressionLedger(configuration);

        services.AddOptions<DeliveryTrackingOptions>()
            .Bind(configuration.GetSection(DeliveryTrackingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ProviderEventDedupePurgeOptions>()
            .Bind(configuration.GetSection(ProviderEventDedupePurgeOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<DeliveryStateApplier>();
        services.AddScoped<IPoisonMessageSink, DeliveryTrackingPoisonMessageSink>();

        // The ledger the ingestion writes has to shrink somewhere, and the
        // role that owns the data owns its retention. Every round is
        // idempotent, so several replicas cost a repeated delete of nothing.
        services.AddScoped<ProviderEventDedupePurge>();
        services.AddHostedService<ProviderEventDedupePurgeService>();

        // The scheduler of this role: the scans that ask for the next plan
        // step when no provider answer arrived, and the one that hands parked
        // notifications back to the pipeline. Every round claims its own rows
        // in the database and keeps nothing in the process, which is what
        // makes more than one replica of this role safe and what makes the
        // second replica useful rather than merely tolerated: a stopped
        // scheduler stops rescuing deliveries and raises nothing at all.
        services.AddOptions<SchedulerScanOptions>()
            .Bind(configuration.GetSection(SchedulerScanOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<SchedulerScanHeartbeat>();
        services.AddScoped<OverdueFallbackScan>();
        services.AddScoped<DeferredReleaseScan>();
        services.AddScoped<PendingSuppressionDrain>();
        services.AddHostedService<SchedulerScanService>();
        services.AddHealthChecks().Add(new HealthCheckRegistration(
            SchedulerScanHealthCheck.Name,
            provider => new SchedulerScanHealthCheck(
                provider.GetRequiredService<SchedulerScanHeartbeat>(),
                provider.GetRequiredService<IOptions<SchedulerScanOptions>>()),
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"]));

        services.AddSqsQueueConsumer<DeliveryEventMessageProcessor>(Queues);
    }
}
