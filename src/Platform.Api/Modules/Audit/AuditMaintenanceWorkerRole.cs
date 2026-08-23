using NotificationHub.Api.Composition;
using NotificationHub.Api.Modules.Audit.Infrastructure.AuditTrail;
using NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;
using NotificationHub.Api.Modules.Audit.Infrastructure.Worm;
using NotificationHub.Api.Modules.Audit.Integration.V1;

namespace NotificationHub.Api.Modules.Audit;

/// <summary>
/// Composition of the <c>audit-maintenance</c> worker role, owned by this
/// module: provisioning of the months ahead, the daily WORM export, the
/// closing cycle of finished partitions, and the periodic chain verification.
/// </summary>
/// <remarks>
/// These jobs live in one role, and in exactly one, on purpose. They revoke
/// grants, detach partitions and write immutable evidence, which is not work
/// that may run once per request-serving replica. The request-serving host
/// keeps only the partition-coverage health check, because it still needs to
/// see the coverage running out; everything that acts is here.
/// </remarks>
public sealed class AuditMaintenanceWorkerRole : IWorkerRoleModule
{
    public static string Role => "audit-maintenance";

    public static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuditPersistence(configuration);
        services.AddSingleton<IAuditTrail, TransactionalAuditTrail>();
        services.AddAuditWormExport(configuration);
        services.AddAuditPartitionMaintenance(configuration);
        services.AddAuditChainVerification(configuration);
    }
}
