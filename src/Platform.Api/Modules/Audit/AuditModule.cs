using Microsoft.Extensions.DependencyInjection.Extensions;
using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit;

public sealed class AuditModule : IModule
{
    public static void ConfigureServices(
        IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAuditPersistence(configuration);
        services.AddAuditPartitionManager(configuration);
        services.TryAddSingleton(TimeProvider.System);

        // Stateless by design: every write joins the caller's transaction.
        services.AddSingleton<IAuditTrail, TransactionalAuditTrail>();
    }
}
