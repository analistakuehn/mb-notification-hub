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

        // Health only: the maintenance jobs (provisioning, export, closing
        // cycle, verification) belong to the audit-maintenance worker role.
        // A request-serving host still needs to see the partition coverage
        // running out, but it must never be the one revoking grants or
        // detaching partitions.
        services.AddAuditPartitionHealth(configuration);
        services.TryAddSingleton(TimeProvider.System);

        // Stateless by design: every write joins the caller's transaction.
        services.AddSingleton<IAuditTrail, TransactionalAuditTrail>();
    }
}
