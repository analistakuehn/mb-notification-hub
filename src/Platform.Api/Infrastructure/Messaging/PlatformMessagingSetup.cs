using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// Composition surface of the platform messaging infrastructure: the outbox
/// context with its own migration history and the transactional writer every
/// producing module appends through. Public on purpose: both hosts compose
/// it, the API for the producing modules and the worker for the relay.
/// </summary>
public static class PlatformMessagingSetup
{
    public static IServiceCollection AddPlatformMessaging(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<PlatformMessagingEfOptions>()
            .Bind(configuration.GetSection(PlatformMessagingEfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<PlatformMessagingDbContext>((serviceProvider, options) =>
        {
            PlatformMessagingEfOptions efOptions =
                serviceProvider.GetRequiredService<IOptions<PlatformMessagingEfOptions>>().Value;
            options.UseNpgsql(efOptions.ConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"));

            if (efOptions.EnableSensitiveDataLogging)
            {
                options.EnableSensitiveDataLogging();
            }

            if (efOptions.EnableDetailedErrors)
            {
                options.EnableDetailedErrors();
            }
        });

        services.TryAddSingleton(TimeProvider.System);

        // Stateless by design: every append joins the caller's transaction.
        services.AddSingleton<IOutboxWriter, TransactionalOutboxWriter>();
        return services;
    }
}
