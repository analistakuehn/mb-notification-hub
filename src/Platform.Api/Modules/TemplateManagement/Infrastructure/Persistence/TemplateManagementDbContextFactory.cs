using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> CLI commands (migrations add,
/// database update). Secrets remain outside committed settings.
/// </summary>
public sealed class TemplateManagementDbContextFactory : IDesignTimeDbContextFactory<TemplateManagementDbContext>
{
    public TemplateManagementDbContext CreateDbContext(string[] args)
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

        var connectionString = configuration[$"{EfOptions.SectionName}:ConnectionString"]
            ?? throw new InvalidOperationException(
                $"Missing configuration '{EfOptions.SectionName}:ConnectionString'.");

        DbContextOptions<TemplateManagementDbContext> options = new DbContextOptionsBuilder<TemplateManagementDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "templatemanagement"))
            .Options;

        return new TemplateManagementDbContext(options);
    }
}
