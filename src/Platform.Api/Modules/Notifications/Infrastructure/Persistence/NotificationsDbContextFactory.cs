using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> CLI commands (migrations add,
/// database update). Secrets remain outside committed settings.
/// </summary>
public sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args)
    {
        var currentDirectory = Directory.GetCurrentDirectory();
        var basePath = File.Exists("appsettings.json")
            ? currentDirectory
            : $"{currentDirectory}/src/Platform.Api";

        IConfigurationRoot configuration = new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connectionString = configuration[$"{NotificationsEfOptions.SectionName}:ConnectionString"]
            ?? throw new InvalidOperationException(
                $"Missing configuration '{NotificationsEfOptions.SectionName}:ConnectionString'.");

        DbContextOptions<NotificationsDbContext> options = new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "notifications"))
            .Options;

        return new NotificationsDbContext(options);
    }
}
