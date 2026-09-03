using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

/// <summary>
/// Design-time factory used by <c>dotnet ef</c> CLI commands (migrations add,
/// database update). Secrets remain outside committed settings.
/// </summary>
public sealed class AttachmentManagementDbContextFactory
    : IDesignTimeDbContextFactory<AttachmentManagementDbContext>
{
    public AttachmentManagementDbContext CreateDbContext(string[] args)
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

        var connectionString = configuration[$"{AttachmentManagementEfOptions.SectionName}:ConnectionString"]
            ?? throw new InvalidOperationException(
                $"Missing configuration '{AttachmentManagementEfOptions.SectionName}:ConnectionString'.");

        DbContextOptions<AttachmentManagementDbContext> options =
            new DbContextOptionsBuilder<AttachmentManagementDbContext>()
                .UseNpgsql(connectionString, npgsql =>
                    npgsql.MigrationsHistoryTable(
                        "__EFMigrationsHistory",
                        "attachmentmanagement"))
                .Options;

        return new AttachmentManagementDbContext(options);
    }
}
