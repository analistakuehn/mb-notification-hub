using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence.Configurations;

internal sealed class AttachmentRevocationConfiguration
    : IEntityTypeConfiguration<AttachmentRevocation>
{
    public void Configure(EntityTypeBuilder<AttachmentRevocation> builder)
    {
        builder.ToTable("attachment_revocation");

        builder.Property(revocation => revocation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(revocation => revocation.Id);

        builder.Property(revocation => revocation.AttachmentId)
            .HasColumnName("attachment_id");

        builder.Property(revocation => revocation.ReleaseId)
            .HasColumnName("release_id");

        builder.Property(revocation => revocation.Reason)
            .HasColumnName("reason")
            .HasMaxLength(AttachmentRevocation.MaxReasonLength);

        builder.Property(revocation => revocation.RevokedAt)
            .HasColumnName("revoked_at");

        // A row arrives complete, and the freeze covers the same two of the
        // four revision paths it covers on the release and the generation
        // rows: a reassignment on a tracked instance throws inside the save,
        // and so does marking the property modified through the entry. An
        // update of a detached instance is still dropped in silence, and a
        // set-based update still rewrites the durable value. The guard for
        // those two lives in the database and does not exist yet, because the
        // module has no migration.
        Freeze(
            builder,
            nameof(AttachmentRevocation.AttachmentId),
            nameof(AttachmentRevocation.ReleaseId),
            nameof(AttachmentRevocation.Reason),
            nameof(AttachmentRevocation.RevokedAt));

        builder.HasIndex(revocation => revocation.AttachmentId)
            .HasDatabaseName("ix_attachment_revocation_attachment");

        // Unique, and this one is the point. A release is granted once and
        // taken back at most once, so the storage itself refuses a second
        // withdrawal of the same grant: the state machine already refuses it,
        // and this is what stands when two callers reach the transition on two
        // connections and both read a state that is about to stop being true.
        builder.HasIndex(revocation => revocation.ReleaseId)
            .HasDatabaseName("ux_attachment_revocation_release")
            .IsUnique();

        builder.HasOne<Attachment>()
            .WithMany()
            .HasForeignKey(revocation => revocation.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AttachmentRelease>()
            .WithMany()
            .HasForeignKey(revocation => revocation.ReleaseId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    private static void Freeze(
        EntityTypeBuilder<AttachmentRevocation> builder,
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
