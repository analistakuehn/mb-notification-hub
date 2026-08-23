using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

public static class AuditEfSetup
{
    public static IServiceCollection AddAuditPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AuditEfOptions>()
            .Bind(configuration.GetSection(AuditEfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<AuditDbContext>((serviceProvider, options) =>
        {
            ConfigureDbContextOptions(
                options,
                serviceProvider.GetRequiredService<IOptions<AuditEfOptions>>().Value);
        });

        return services;
    }

    private static void ConfigureDbContextOptions(
        DbContextOptionsBuilder options,
        AuditEfOptions efOptions)
    {
        options.UseNpgsql(efOptions.ConnectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "audit"));

        if (efOptions.EnableSensitiveDataLogging)
        {
            options.EnableSensitiveDataLogging();
        }

        if (efOptions.EnableDetailedErrors)
        {
            options.EnableDetailedErrors();
        }
    }
}
