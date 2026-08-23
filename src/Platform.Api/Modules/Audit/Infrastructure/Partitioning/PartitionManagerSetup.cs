using Microsoft.Extensions.Options;
using NotificationHub.Api.Infrastructure.Partitioning;
using NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Partitioning;

public static class PartitionManagerSetup
{
    public static IServiceCollection AddAuditPartitionManager(
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

        services.AddScoped<PartitionMaintenance>();
        services.AddHostedService<PartitionManagerService>();
        services.AddHealthChecks()
            .AddMonthlyPartitionCoverageCheck<AuditDbContext>(
                name: "audit-partitions",
                schema: "audit",
                table: "audit_event",
                minimumFutureDays: serviceProvider => serviceProvider
                    .GetRequiredService<IOptions<PartitionManagerOptions>>()
                    .Value.FutureWindowMinimumDays);
        return services;
    }
}
