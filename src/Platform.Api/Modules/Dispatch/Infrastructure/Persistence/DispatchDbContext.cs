using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Dispatch.Domain;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence;

/// <summary>
/// Persistence boundary owned exclusively by the Dispatch bounded context:
/// the materialized provider configuration. The application reads it; the
/// infrastructure deploy job writes it.
/// </summary>
public sealed class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public DbSet<ProviderSelection> ProviderSelections => Set<ProviderSelection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("dispatch");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(DispatchDbContext).Assembly,
            type => type.Namespace?.StartsWith(
                "NotificationHub.Api.Modules.Dispatch",
                StringComparison.Ordinal) is true);
        base.OnModelCreating(modelBuilder);
    }
}
