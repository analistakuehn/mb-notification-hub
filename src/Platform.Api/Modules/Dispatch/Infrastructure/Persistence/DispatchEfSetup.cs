using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;

public static class DispatchEfSetup
{
    public static IServiceCollection AddDispatchPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DispatchEfOptions>()
            .Bind(configuration.GetSection(DispatchEfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<DispatchDbContext>((serviceProvider, options) =>
        {
            ConfigureDbContextOptions(
                options,
                serviceProvider.GetRequiredService<IOptions<DispatchEfOptions>>().Value);
        });

        return services;
    }

    private static void ConfigureDbContextOptions(
        DbContextOptionsBuilder options,
        DispatchEfOptions efOptions)
    {
        options.UseNpgsql(efOptions.ConnectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "dispatch"));

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
