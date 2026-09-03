using Microsoft.EntityFrameworkCore;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence;

public sealed class AttachmentManagementDbContext(
    DbContextOptions<AttachmentManagementDbContext> options)
    : DbContext(options)
{
    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<ProducerApplicationGrant> ProducerApplicationGrants
        => Set<ProducerApplicationGrant>();

    internal DbSet<AttachmentObjectGeneration> ObjectGenerations
        => Set<AttachmentObjectGeneration>();

    internal DbSet<AttachmentDependency> AttachmentDependencies
        => Set<AttachmentDependency>();

    internal DbSet<AttachmentRelease> Releases => Set<AttachmentRelease>();

    internal DbSet<AttachmentRevocation> Revocations => Set<AttachmentRevocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("attachmentmanagement");
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(AttachmentManagementDbContext).Assembly,
            type => type.Namespace is { } typeNamespace
                && (typeNamespace.Equals(
                    "NotificationHub.Api.Modules.AttachmentManagement",
                    StringComparison.Ordinal)
                || typeNamespace.StartsWith(
                    "NotificationHub.Api.Modules.AttachmentManagement.",
                    StringComparison.Ordinal)));
        base.OnModelCreating(modelBuilder);
    }
}
