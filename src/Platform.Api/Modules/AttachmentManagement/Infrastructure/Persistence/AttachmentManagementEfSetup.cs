using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

public static class AttachmentManagementEfSetup
{
    public static IServiceCollection AddAttachmentManagementPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<AttachmentManagementEfOptions>()
            .Bind(configuration.GetSection(AttachmentManagementEfOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => !options.EnableSensitiveDataLogging,
                $"{nameof(AttachmentManagementEfOptions.EnableSensitiveDataLogging)} must be false.")
            .ValidateOnStart();

        services.AddDbContextFactory<AttachmentManagementDbContext>((serviceProvider, options) =>
        {
            ConfigureDbContextOptions(
                options,
                serviceProvider.GetRequiredService<IOptions<AttachmentManagementEfOptions>>().Value);
        });
        services.AddScoped<IAttachmentSaveOperation, AttachmentSaveOperation>();

        return services;
    }

    private static void ConfigureDbContextOptions(
        DbContextOptionsBuilder options,
        AttachmentManagementEfOptions efOptions)
    {
        options.UseNpgsql(efOptions.ConnectionString, npgsql =>
            npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "attachmentmanagement"));

        if (efOptions.EnableDetailedErrors)
        {
            options.EnableDetailedErrors();
        }
    }
}
