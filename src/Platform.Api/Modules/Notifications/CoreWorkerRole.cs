using NotificationHub.Api.Composition;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Infrastructure.Messaging;
using NotificationHub.Api.Infrastructure.Messaging.Consuming;
using NotificationHub.Api.Infrastructure.Messaging.Relay;
using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Consuming;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications;

/// <summary>
/// Configuration of the core role: which core queues this instance drains.
/// Empty selects all of them; a dedicated instance restricts to a subset,
/// mirroring the relay's band restriction. The drain priority never changes:
/// it lives in the slot allocation, not in this list.
/// </summary>
public sealed class CoreWorkerOptions
{
    public const string SectionName = "Modules:Notifications:CoreWorker";

    /// <summary>Band names among auth, critical, transactional, operational; empty means all.</summary>
    public string[] Bands { get; init; } = [];
}

/// <summary>
/// Composition of the <c>core</c> worker role, owned by this module: the four
/// core queues consumed concurrently with processing slots prioritized
/// auth over critical over transactional over operational, the stage
/// pipeline in its fixed order, and the read surfaces of the sibling
/// contexts, each behind its published contract.
/// </summary>
public sealed class CoreWorkerRole : IWorkerRoleModule
{
    public static string Role => "core";

    /// <summary>Core queue of each band, in slot-priority order.</summary>
    internal static readonly (OutboxBand Band, string Queue)[] BandQueues =
    [
        (OutboxBand.Auth, "core-auth"),
        (OutboxBand.Critical, "core-critical"),
        (OutboxBand.Transactional, "core-transactional"),
        (OutboxBand.Operational, "core-operational"),
    ];

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddPlatformMessaging(configuration);
        services.AddSqsMessageConsuming(configuration);
        services.AddEnvelopeEncryption(configuration);
        services.AddAuditTrailSurface();
        services.AddTemplateManagementReadSurface(configuration);
        services.AddContactConsentReadSurface(configuration);
        services.AddNotificationsPersistence(configuration);
        services.AddNotificationsKillSwitch();
        services.AddNotificationsKillSwitchHolds();

        // The provider surface of Dispatch, for one question and no send: a
        // notification carrying an accepted set may only be planned onto a
        // channel whose adapter composes that set. The answer has to come from
        // the adapter this deployment would call, so this role hosts the same
        // adapters the dispatcher does and calls none of them. A table of
        // channels kept here instead would be a second statement about their
        // behaviour, and it would keep saying yes after a channel was pointed
        // at an adapter that carries nothing.
        services.AddDispatchProviderSurface(configuration);

        services.AddOptions<NotificationsRedisOptions>()
            .Bind(configuration.GetSection(NotificationsRedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<NotificationsRedisConnection>();
        services.AddSingleton<IDedupeBarrier, RedisDedupeBarrier>();

        services.AddScoped<PublishedTemplateGate>();
        services.AddScoped<ValidateStage>();
        services.AddScoped<ResolveStage>();
        services.AddScoped<ConsentGateRule>();
        services.AddScoped<SuppressionGateRule>();
        services.AddScoped<QuietHoursRule>();
        services.AddScoped<DedupeWindowRule>();
        services.AddScoped<ChannelSelectionRule>();
        services.AddScoped(serviceProvider => new PolicyStage(
            serviceProvider.GetRequiredService<IPublishedCatalog>(),
            RulesInOrder(serviceProvider),
            serviceProvider.GetRequiredService<TimeProvider>()));
        services.AddScoped<RenderStage>();
        services.AddScoped<RouteStage>();
        services.AddScoped(serviceProvider => new NotificationPipeline(StagesInOrder(serviceProvider)));
        services.AddScoped<CoreMessageHandlers>();
        services.AddScoped<PipelineCommitWriter>();
        services.AddScoped<FallbackRequestHandler>();
        services.AddScoped<IPoisonMessageSink, CorePoisonMessageSink>();

        services.AddSqsQueueConsumer<CoreMessageProcessor>(QueueBindings(configuration));
    }

    /// <summary>
    /// The rule order of the policy stage; changing it is a policy decision,
    /// not a refactor. The suppression gate sits after consent, because a
    /// recipient who never allowed the channel is refused for a stronger
    /// reason, and before the silence window, because deferring a notification
    /// for hours to reject it in the morning is work nobody asked for.
    /// </summary>
    internal static IPolicyRule<NotificationContext>[] RulesInOrder(IServiceProvider serviceProvider) =>
    [
        serviceProvider.GetRequiredService<ConsentGateRule>(),
        serviceProvider.GetRequiredService<SuppressionGateRule>(),
        serviceProvider.GetRequiredService<QuietHoursRule>(),
        serviceProvider.GetRequiredService<DedupeWindowRule>(),
        serviceProvider.GetRequiredService<ChannelSelectionRule>(),
    ];

    /// <summary>The stage order of the pipeline; the commit runs after the last stage, inside the run.</summary>
    private static INotificationStage[] StagesInOrder(IServiceProvider serviceProvider) =>
    [
        serviceProvider.GetRequiredService<ValidateStage>(),
        serviceProvider.GetRequiredService<ResolveStage>(),
        serviceProvider.GetRequiredService<PolicyStage>(),
        serviceProvider.GetRequiredService<RenderStage>(),
        serviceProvider.GetRequiredService<RouteStage>(),
    ];

    /// <summary>
    /// The queues this instance drains, restricted by the configured bands.
    /// The slot-priority rank is the band's fixed position, whatever subset
    /// the configuration selects; an unknown band name refuses to boot.
    /// </summary>
    internal static SqsQueueBinding[] QueueBindings(IConfiguration configuration)
    {
        CoreWorkerOptions options = configuration.GetSection(CoreWorkerOptions.SectionName).Get<CoreWorkerOptions>()
            ?? new CoreWorkerOptions();
        foreach (var name in options.Bands)
        {
            if (!OutboxBands.TryParseName(name, out _))
            {
                throw new InvalidOperationException(
                    $"Banda desconhecida '{name}' em '{CoreWorkerOptions.SectionName}:Bands'. "
                    + "Bandas conhecidas: auth, critical, transactional, operational.");
            }
        }

        OutboxBand[] selected = OutboxBands.Restrict(options.Bands);
        return [.. BandQueues
            .Where(entry => selected.Contains(entry.Band))
            .Select(entry => new SqsQueueBinding(entry.Queue, (int)entry.Band))];
    }
}
