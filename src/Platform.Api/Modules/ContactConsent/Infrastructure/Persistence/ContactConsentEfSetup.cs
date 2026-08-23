using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence;

public static class ContactConsentEfSetup
{
    public static IServiceCollection AddContactConsentPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ContactConsentEfOptions>()
            .Bind(configuration.GetSection(ContactConsentEfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<ContactConsentDbContext>((serviceProvider, options) =>
        {
            ContactConsentEfOptions efOptions =
                serviceProvider.GetRequiredService<IOptions<ContactConsentEfOptions>>().Value;
            options.UseNpgsql(efOptions.ConnectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "contactconsent"));

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
