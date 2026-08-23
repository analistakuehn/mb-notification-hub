using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Export;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

public static class PartitionManagerSetup
{
    /// <summary>
    /// Options and the coverage health check, without hosting anything. Every
    /// host that serves audited effects registers this: a host that cannot see
    /// the coverage running out would keep accepting effects until the first
    /// insert of a missing month fails.
    /// </summary>
    public static IServiceCollection AddAuditPartitionHealth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PartitionManagerOptions>()
            .Bind(configuration.GetSection(PartitionManagerOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => options.Interval >= TimeSpan.FromMinutes(1),
                "O intervalo do gerenciador de partições deve ser de pelo menos um minuto.")
            .Validate(
                options => options.Interval <= TimeSpan.FromDays(30),
                "O intervalo do gerenciador de partições deve ser de no máximo trinta dias; acima disso a provisão mensal perde rodadas.")
            .Validate(
                options => options.PartitionedTables.All(PartitionManagerOptions.IsSafeTableName),
                "A lista de tabelas particionadas deve conter apenas identificadores PostgreSQL em minúsculas (letras, dígitos e sublinhado).")
            .ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddHealthChecks()
            .AddMonthlyPartitionCoverageCheck<AuditDbContext>(
                name: "audit-partitions",
                schema: AuditPartitionCatalog.Schema,
                table: AuditPartitionCatalog.Table,
                minimumFutureDays: serviceProvider => serviceProvider
                    .GetRequiredService<IOptions<PartitionManagerOptions>>()
                    .Value.FutureWindowMinimumDays);
        return services;
    }

    /// <summary>
    /// The maintenance job itself: provisioning, daily export and the closing
    /// cycle, hosted by the maintenance worker role alone. Keeping it out of
    /// the request-serving host is what stops a partition from being detached
    /// by whichever instance happened to boot first.
    /// </summary>
    internal static IServiceCollection AddAuditPartitionMaintenance(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddAuditPartitionHealth(configuration);
        services.AddScoped<AuditPartitionCatalog>();
        services.AddScoped<AuditMaintenanceLock>();
        services.AddScoped<ClosedPartitionGuard>();
        services.AddScoped<PartitionClosingCycle>();
        services.AddScoped<PartitionMaintenance>();
        services.AddScoped<PartitionMaintenanceRound>();
        services.AddScoped<AuditTrailReader>();
        services.AddHostedService<PartitionManagerService>();
        return services;
    }
}
