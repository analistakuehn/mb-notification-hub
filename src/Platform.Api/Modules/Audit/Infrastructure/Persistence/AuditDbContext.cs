using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.Audit.Domain;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Persistence;

/// <summary>
/// Persistence boundary owned exclusively by the Audit bounded context. The
/// context exists for migrations, maintenance and reads; appends flow through
/// the transactional trail contract, on the caller's own transaction.
/// </summary>
public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options) : DbContext(options)
{
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();

    public DbSet<Approval> Approvals => Set<Approval>();

    /// <summary>Progress of the periodic chain verification; job state, never trail content.</summary>
    internal DbSet<ChainVerificationCheckpoint> ChainVerificationCheckpoints
        => Set<ChainVerificationCheckpoint>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("audit");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AuditDbContext).Assembly,
            type => type.Namespace?.StartsWith(
                "NotificationHub.Api.Modules.Audit",
                StringComparison.Ordinal) is true);
        base.OnModelCreating(modelBuilder);
    }
}
