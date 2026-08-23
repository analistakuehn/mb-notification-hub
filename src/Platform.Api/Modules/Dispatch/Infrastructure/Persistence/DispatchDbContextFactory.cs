using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> CLI commands (migrations add,
/// database update). Secrets remain outside committed settings.
/// </summary>
public sealed class DispatchDbContextFactory : IDesignTimeDbContextFactory<DispatchDbContext>
{
    public DispatchDbContext CreateDbContext(string[] args)
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

        var connectionString = configuration[$"{DispatchEfOptions.SectionName}:ConnectionString"]
            ?? throw new InvalidOperationException(
                $"Missing configuration '{DispatchEfOptions.SectionName}:ConnectionString'.");

        DbContextOptions<DispatchDbContext> options = new DbContextOptionsBuilder<DispatchDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "dispatch"))
            .Options;

        return new DispatchDbContext(options);
    }
}
