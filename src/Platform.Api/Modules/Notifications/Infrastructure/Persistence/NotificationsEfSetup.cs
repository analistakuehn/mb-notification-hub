using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

public static class NotificationsEfSetup
{
    public static IServiceCollection AddNotificationsPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<NotificationsEfOptions>()
            .Bind(configuration.GetSection(NotificationsEfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<NotificationsDbContext>((serviceProvider, options) =>
        {
            NotificationsEfOptions efOptions =
                serviceProvider.GetRequiredService<IOptions<NotificationsEfOptions>>().Value;
            options.UseNpgsql(efOptions.ConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"));

            if (efOptions.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }

            if (efOptions.EnableDetailedErrors)
            {
                options.EnableDetailedErrors();
            }
        });

        // The query surface resolves this one: same model, its own connection,
        // no tracking and no write entry point. It is registered beside the
        // write context so a host that composes persistence gets both.
        services.AddDbContext<NotificationsReadDbContext>((serviceProvider, options) =>
        {
            NotificationsEfOptions efOptions =
                serviceProvider.GetRequiredService<IOptions<NotificationsEfOptions>>().Value;
            options.UseNpgsql(efOptions.EffectiveReadConnectionString);

            if (efOptions.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }

            if (efOptions.EnableDetailedErrors)
            {
                options.EnableDetailedErrors();
            }
        });

        return services;
    }
}
