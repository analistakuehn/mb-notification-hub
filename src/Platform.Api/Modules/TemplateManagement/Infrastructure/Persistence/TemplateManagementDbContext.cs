using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence;

/// <summary>
/// Persistence boundary owned exclusively by the TemplateManagement bounded context.
/// </summary>
public sealed class TemplateManagementDbContext(DbContextOptions<TemplateManagementDbContext> options) : DbContext(options)
{
    public DbSet<Template> Templates => Set<Template>();

    public DbSet<TemplateVersion> TemplateVersions => Set<TemplateVersion>();

    public DbSet<Approval> Approvals => Set<Approval>();

    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

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
