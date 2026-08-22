using Microsoft.EntityFrameworkCore;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

/// <summary>
/// Persistence boundary owned exclusively by the TemplateManagement bounded context.
/// </summary>
public sealed class TemplateManagementDbContext(DbContextOptions<TemplateManagementDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("templatemanagement");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(TemplateManagementDbContext).Assembly,
            type => type.Namespace?.StartsWith(
                "NotificationHub.Api.Modules.TemplateManagement",
                StringComparison.Ordinal) is true);
        base.OnModelCreating(modelBuilder);
    }
}
