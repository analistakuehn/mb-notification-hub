using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.Compliance.Features.Reporting;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Devices;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Reads;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Redis;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Suppression;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.ProviderConfig;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Templating;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Composition;

/// <summary>
/// Composition of the published read surfaces a worker role consumes from
/// sibling contexts: exactly the persistence and services each contract
/// needs, no endpoints, no hosted jobs. This lives in the composition root on
/// purpose: a module cannot compose another module's internals, and the
/// worker host cannot reference modules, so the root wires what a role
/// declares it consumes.
/// </summary>
public static class IntegrationSurfaceSetup
{
    /// <summary>The transactional audit trail contract; stateless, joins the caller's transaction.</summary>
    public static IServiceCollection AddAuditTrailSurface(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAuditTrail, TransactionalAuditTrail>();
        return services;
    }

    /// <summary>
    /// The published catalog, variables validation and renderer of
    /// TemplateManagement, with the module's memoization behind them.
    /// </summary>
    public static IServiceCollection AddTemplateManagementReadSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddEntityFramework(configuration);
        services.AddTemplateManagementTemplating(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<PublishedReadCache>();
        services.TryAddScoped<PublishedContextLoader>();
        services.TryAddScoped<IPublishedCatalog, PublishedCatalog>();
        services.TryAddScoped<IPublishedVariablesValidator, PublishedVariablesValidator>();
        services.TryAddScoped<IPublishedTemplateRenderer, PublishedTemplateRenderer>();
        return services;
    }

    /// <summary>
    /// The recipient directory of ContactConsent behind its encrypted
    /// cache-aside layer, with the module's own Redis connection.
    /// </summary>
    public static IServiceCollection AddContactConsentReadSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddContactConsentPersistence(configuration);
        services.AddOptions<ContactConsentRedisOptions>()
            .Bind(configuration.GetSection(ContactConsentRedisOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ContactConsentRedisConnection>();
        services.TryAddSingleton<RecipientSnapshotCache>();
        services.TryAddScoped<ContactValueProtector>();
        services.TryAddScoped<RecipientDirectory>();
        services.TryAddScoped<IRecipientDirectory, CachedRecipientDirectory>();
        return services;
    }

    /// <summary>
    /// The device-token lifecycle of ContactConsent for the provider feedback
    /// path, over the module's own persistence and transactional writer. The
    /// consuming role also composes the platform messaging and the audit
    /// trail surface, because every lifecycle write commits its outbox
    /// message and its audit event transactionally.
    /// </summary>
    public static IServiceCollection AddContactConsentDeviceLifecycle(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddContactConsentPersistence(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ContactConsentWriter>();
        services.TryAddScoped<IDeviceTokenLifecycle, DeviceTokenInvalidation>();
        return services;
    }

    /// <summary>
    /// The suppression ledger of ContactConsent for the delivery-feedback
    /// path, over the module's own persistence and transactional writer. The
    /// consuming role also composes the platform messaging and the audit trail
    /// surface, because every ledger write commits its outbox messages and its
    /// audit event transactionally.
    /// </summary>
    public static IServiceCollection AddContactConsentSuppressionLedger(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddContactConsentPersistence(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ContactConsentWriter>();
        services.TryAddScoped<ISuppressionLedger, SuppressionLedger>();
        return services;
    }

    /// <summary>
    /// The delivery-lookup surface of Dispatch: the adapters that ask a
    /// provider what became of a message whose feedback never arrived, and the
    /// resolution that picks between them.
    /// <para>
    /// It binds the sending credentials and the callback vocabulary, and it
    /// composes neither send adapters nor callback interpreters. A lookup
    /// authenticates with the credentials of the send and classifies failure
    /// codes with the vocabulary of the callback, because a code that means a
    /// dead destination means the same whether this hub was told or had to
    /// ask; what it does not need is the ability to send anything, and a batch
    /// role that cannot send is a batch role that cannot message anybody by
    /// accident.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDispatchDeliveryLookupSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<SendGridOptions>()
            .Bind(configuration.GetSection(SendGridOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<TwilioOptions>()
            .Bind(configuration.GetSection(TwilioOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<SendGridWebhookOptions>()
            .Bind(configuration.GetSection(SendGridWebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<TwilioWebhookOptions>()
            .Bind(configuration.GetSection(TwilioWebhookOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.AddDispatchDeliveryLookups();
        return services;
    }

    /// <summary>
    /// The recurring evidence report of Compliance, with the Notifications
    /// read surface it aggregates over. The composing module is a leaf of the
    /// dependency graph and owns no store, so the root is what puts the read
    /// behind its published contract; the archive and the trail read come from
    /// the module whose role hosts this job.
    /// </summary>
    public static IServiceCollection AddComplianceEvidenceReporting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddNotificationsPersistence(configuration);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<INotificationOutcomeReport, NotificationOutcomeReader>();
        services.AddComplianceMonthlyEvidenceReport(configuration);
        return services;
    }

    /// <summary>
    /// The channel-provider surface of Dispatch: the hosted adapters, their
    /// resilience pipelines, and the resolution over the materialized
    /// provider configuration. Exactly what the Dispatch module composes in
    /// the API host, for the worker role that performs sends.
    /// </summary>
    public static IServiceCollection AddDispatchProviderSurface(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddDispatchPersistence(configuration);
        services.AddDispatchProviders(configuration);
        services.AddDispatchProviderResolution(configuration);
        services.TryAddSingleton(TimeProvider.System);
        return services;
    }
}
