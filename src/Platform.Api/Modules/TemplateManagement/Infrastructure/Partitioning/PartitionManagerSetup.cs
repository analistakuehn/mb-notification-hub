namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Partitioning;

public static class PartitionManagerSetup
{
    public static IServiceCollection AddTemplateManagementPartitionManager(
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
                options => options.PartitionedTables.Count > 0
                    && options.PartitionedTables.All(PartitionManagerOptions.IsSafeTableName),
                "A lista de tabelas particionadas deve conter apenas identificadores PostgreSQL em minúsculas (letras, dígitos e sublinhado).")
            .ValidateOnStart();

        services.AddScoped<PartitionMaintenance>();
        services.AddHostedService<PartitionManagerService>();
        return services;
    }
}
