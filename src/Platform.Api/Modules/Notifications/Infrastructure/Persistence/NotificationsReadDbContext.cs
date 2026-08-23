using Microsoft.EntityFrameworkCore;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence;

/// <summary>
/// The query side of this module's persistence: the same model over a
/// connection of its own, so the query surface can be pointed at a read
/// replica without touching the write path. Tracking is off by default,
/// because nothing here materializes an entity to change it, and every write
/// entry point throws: the type is the guardrail that keeps a query from
/// mutating state, which is the property the audit surface will depend on.
/// Migrations never run through this context, and the design-time factory
/// never builds it.
/// </summary>
public sealed class NotificationsReadDbContext : NotificationsDbContext
{
    public NotificationsReadDbContext(DbContextOptions<NotificationsReadDbContext> options)
        : base(options)
        => ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;

    public override int SaveChanges() => throw ReadOnlyViolation();

    public override int SaveChanges(bool acceptAllChangesOnSuccess) => throw ReadOnlyViolation();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => throw ReadOnlyViolation();

    public override Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
        => throw ReadOnlyViolation();

    private static InvalidOperationException ReadOnlyViolation()
        => new("O contexto de leitura de notificações não grava: use o contexto de escrita do módulo.");
}
