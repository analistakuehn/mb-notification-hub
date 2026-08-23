using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> CLI commands (migrations add,
/// database update). Secrets remain outside committed settings.
/// </summary>
public sealed class PlatformMessagingDbContextFactory : IDesignTimeDbContextFactory<PlatformMessagingDbContext>
{
    public PlatformMessagingDbContext CreateDbContext(string[] args)
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

        var connectionString = configuration[$"{PlatformMessagingEfOptions.SectionName}:ConnectionString"]
            ?? throw new InvalidOperationException(
                $"Missing configuration '{PlatformMessagingEfOptions.SectionName}:ConnectionString'.");

        DbContextOptions<PlatformMessagingDbContext> options =
            new DbContextOptionsBuilder<PlatformMessagingDbContext>()
                .UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "platform"))
                .Options;

        return new PlatformMessagingDbContext(options);
    }
}
