using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// Persistence boundary owned exclusively by the Notifications bounded
/// context (schema <c>notifications</c>). The notification table is
/// partitioned by month on <c>created_at</c>, which is why its primary key is
/// composite; the idempotency table stays outside the partitioning exactly so
/// its unique key can exist.
/// </summary>
public class NotificationsDbContext : DbContext
{
    // Explicit constructors on purpose: the read-only derivation needs a
    // second entry point, and a primary constructor forbids any sibling
    // constructor that does not chain into it.
    public NotificationsDbContext(DbContextOptions<NotificationsDbContext> options)
        : base(options)
    {
    }

    /// <summary>
    /// Entry point of the read-only derivation, which carries its own options
    /// (and therefore its own connection) over this same model. Kept protected
    /// so nothing outside the module can widen the write surface.
    /// </summary>
    protected NotificationsDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationAttempt> NotificationAttempts => Set<NotificationAttempt>();

    public DbSet<PolicyEvaluation> PolicyEvaluations => Set<PolicyEvaluation>();

    public DbSet<IdempotencyRegistration> IdempotencyRegistrations => Set<IdempotencyRegistration>();

    /// <summary>Read-only at runtime: a deploy job materializes the grants from the infrastructure repository.</summary>
    public DbSet<ProducerRegistration> ProducerRegistrations => Set<ProducerRegistration>();

    public DbSet<KillSwitchState> KillSwitches => Set<KillSwitchState>();

    public DbSet<KillSwitchHold> KillSwitchHolds => Set<KillSwitchHold>();

    /// <summary>Provider feedback as received, partitioned by month on its reception instant.</summary>
    internal DbSet<DeliveryEvent> DeliveryEvents => Set<DeliveryEvent>();

    /// <summary>Verified callback bytes, stored once per callback and referenced by its events.</summary>
    internal DbSet<DeliveryPayload> DeliveryPayloads => Set<DeliveryPayload>();

    /// <summary>Deduplication ledger of provider callbacks; unpartitioned so its unique key can exist.</summary>
    internal DbSet<ProviderEventDedupe> ProviderEventDedupes => Set<ProviderEventDedupe>();

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
