using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

public static class EfSetup
{
    public static IServiceCollection AddEntityFramework(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<EfOptions>()
            .Bind(configuration.GetSection(EfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<TemplateManagementDbContext>((serviceProvider, options) =>
        {
            ConfigureDbContextOptions(
                options,
                serviceProvider.GetRequiredService<IOptions<EfOptions>>().Value);
        });

        return services;
    }

    private static void ConfigureDbContextOptions(
        DbContextOptionsBuilder options,
        EfOptions efOptions)
    {
        options.UseNpgsql(efOptions.ConnectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "templatemanagement"));

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
