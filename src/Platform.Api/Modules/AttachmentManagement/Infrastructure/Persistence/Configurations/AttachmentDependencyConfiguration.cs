using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence.Configurations;

internal sealed class AttachmentDependencyConfiguration
    : IEntityTypeConfiguration<AttachmentDependency>
{
    public void Configure(EntityTypeBuilder<AttachmentDependency> builder)
    {
        builder.ToTable("attachment_dependency");

        builder.Property(dependency => dependency.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(dependency => dependency.Id);

        builder.Property(dependency => dependency.AttachmentId)
            .HasColumnName("attachment_id");

        builder.Property(dependency => dependency.Reason)
            .HasColumnName("reason")
            .HasMaxLength(AttachmentDependency.MaxReasonLength);

        builder.Property(dependency => dependency.Holder)
            .HasColumnName("holder")
            .HasMaxLength(AttachmentDependency.MaxHolderLength);

        builder.Property(dependency => dependency.AcquiredAt)
            .HasColumnName("acquired_at");

        builder.Property(dependency => dependency.ReleasedAt)
            .HasColumnName("released_at");

        builder.Property(dependency => dependency.Version)
            .HasColumnName("version");

        // One row per dependent per attachment, so taking the same dependency
        // twice cannot leave a second hold that nobody knows how to release.
        builder.HasIndex(dependency => new
        {
            dependency.AttachmentId,
            dependency.Holder,
        })
            .HasDatabaseName("ux_attachment_dependency_holder")
            .IsUnique();

        // The only read on the protection path asks whether a live row exists.
        builder.HasIndex(dependency => dependency.AttachmentId)
            .HasDatabaseName("ix_attachment_dependency_live")
            .HasFilter("released_at IS NULL");

        builder.HasOne<Attachment>()
            .WithMany()
            .HasForeignKey(dependency => dependency.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
