using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Persistence boundary owned exclusively by the Notifications bounded
/// context (schema <c>notifications</c>). The notification table is
/// partitioned by month on <c>created_at</c>, which is why its primary key is
/// composite; the idempotency table stays outside the partitioning exactly so
/// its unique key can exist.
/// </summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
    : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();

    public DbSet<PolicyEvaluation> PolicyEvaluations => Set<PolicyEvaluation>();

    public DbSet<IdempotencyRegistration> IdempotencyRegistrations => Set<IdempotencyRegistration>();

    /// <summary>Read-only at runtime: a deploy job materializes the grants from the infrastructure repository.</summary>
    public DbSet<ProducerRegistration> ProducerRegistrations => Set<ProducerRegistration>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("notifications");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(NotificationsDbContext).Assembly,
            type => type.Namespace?.StartsWith(
                "NotificationHub.Api.Modules.Notifications",
                StringComparison.Ordinal) is true);
        base.OnModelCreating(modelBuilder);
    }
}
