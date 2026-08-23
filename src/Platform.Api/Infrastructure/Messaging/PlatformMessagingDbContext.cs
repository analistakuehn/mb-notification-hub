using Microsoft.EntityFrameworkCore;

namespace NotificationHub.Api.Infrastructure.Messaging;

/// <summary>
/// Persistence boundary of the platform messaging infrastructure (schema
/// <c>platform</c>). The context exists for migrations, maintenance and relay
/// reads; production appends flow through <see cref="IOutboxWriter"/>, on the
/// caller's own transaction. No module type ever enters this context.
/// </summary>
public sealed class PlatformMessagingDbContext(DbContextOptions<PlatformMessagingDbContext> options)
    : DbContext(options)
{
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("platform");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(PlatformMessagingDbContext).Assembly,
            type => type.Namespace?.StartsWith(
                "NotificationHub.Api.Infrastructure.Messaging",
                StringComparison.Ordinal) is true);
        base.OnModelCreating(modelBuilder);
    }
}
