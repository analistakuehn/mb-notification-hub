using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence.Configurations;

internal sealed class AttachmentReleaseConfiguration : IEntityTypeConfiguration<AttachmentRelease>
{
    public void Configure(EntityTypeBuilder<AttachmentRelease> builder)
    {
        builder.ToTable("attachment_release");

        builder.Property(release => release.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(release => release.Id);

        builder.Property(release => release.AttachmentId)
            .HasColumnName("attachment_id");

        builder.Property(release => release.GenerationId)
            .HasColumnName("generation_id");

        builder.Property(release => release.ReleasedAt)
            .HasColumnName("released_at");

        builder.Property(release => release.ExpiresAt)
            .HasColumnName("expires_at");

        // A row arrives complete, and the freeze covers the same two of the
        // four revision paths it covers on the generation row: a reassignment
        // on a tracked instance throws inside the save, and so does marking the
        // property modified through the entry. An update of a detached
        // instance is still dropped in silence, and a set-based update still
        // rewrites the durable value. The guard for those two lives in the
        // database and does not exist yet, because the module has no migration.
        Freeze(
            builder,
            nameof(AttachmentRelease.AttachmentId),
            nameof(AttachmentRelease.GenerationId),
            nameof(AttachmentRelease.ReleasedAt),
            nameof(AttachmentRelease.ExpiresAt));

        builder.HasIndex(release => release.AttachmentId)
            .HasDatabaseName("ix_attachment_release_attachment");

        // Deliberately not unique. A unique index here would say that one
        // attachment carries at most one release for as long as the table
        // lives, and an explicit revalidation is supposed to write a second
        // row with an instant of its own.
        builder.HasOne<Attachment>()
            .WithMany()
            .HasForeignKey(release => release.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttachmentObjectGeneration>()
            .WithMany()
            .HasForeignKey(release => release.GenerationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_attachment_release_expiry_after_release",
            "expires_at > released_at"));
    }

    private static void Freeze(
        EntityTypeBuilder<AttachmentRelease> builder,
        params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            builder.Property(propertyName)
                .Metadata
                .SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        }
    }
}
