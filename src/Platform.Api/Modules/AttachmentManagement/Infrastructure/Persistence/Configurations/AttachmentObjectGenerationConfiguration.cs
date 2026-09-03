using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence.Configurations;

internal sealed class AttachmentObjectGenerationConfiguration
    : IEntityTypeConfiguration<AttachmentObjectGeneration>
{
    public void Configure(EntityTypeBuilder<AttachmentObjectGeneration> builder)
    {
        builder.ToTable("attachment_object_generation");

        builder.Property(generation => generation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(generation => generation.Id);

        builder.Property(generation => generation.AttachmentId)
            .HasColumnName("attachment_id");

        builder.Property(generation => generation.Store)
            .HasColumnName("store")
            .HasMaxLength(AttachmentObjectLocator.MaxStoreLength);

        builder.Property(generation => generation.Key)
            .HasColumnName("object_key")
            .HasMaxLength(AttachmentObjectLocator.MaxKeyLength);

        // Sized by the ceiling the provider documents, not by the width the
        // local double happens to emit.
        builder.Property(generation => generation.Version)
            .HasColumnName("version")
            .HasMaxLength(AttachmentObjectLocator.MaxVersionLength);

        builder.Property(generation => generation.Algorithm)
            .HasColumnName("digest_algorithm")
            .HasMaxLength(AttachmentObjectGeneration.MaxAlgorithmLength);

        builder.Property(generation => generation.Digest)
            .HasColumnName("digest");

        builder.Property(generation => generation.LengthBytes)
            .HasColumnName("length_bytes");

        builder.Property(generation => generation.DetectedContentType)
            .HasColumnName("detected_content_type")
            .HasMaxLength(AttachmentObjectGeneration.MaxDetectedContentTypeLength);

        builder.Property(generation => generation.CapturedAt)
            .HasColumnName("captured_at");

        // A row arrives complete. Measured against the database this module
        // runs on, the freeze below refuses two of the four ways a column can
        // be revised and refuses neither of the other two.
        //
        // Refused: reassigning the column on a tracked instance throws inside
        // the save, and marking it modified through the entry throws the same
        // way.
        //
        // Not refused: updating a detached instance does not throw and drops
        // the change in silence, and a set-based update goes around the change
        // tracker and rewrites the durable value.
        //
        // So this is a guard against ordinary code, not against every writer.
        // The guard that covers the other two paths belongs in the database
        // and does not exist yet, because the module has no migration.
        Freeze(
            builder,
            nameof(AttachmentObjectGeneration.AttachmentId),
            nameof(AttachmentObjectGeneration.Store),
            nameof(AttachmentObjectGeneration.Key),
            nameof(AttachmentObjectGeneration.Version),
            nameof(AttachmentObjectGeneration.Algorithm),
            nameof(AttachmentObjectGeneration.Digest),
            nameof(AttachmentObjectGeneration.LengthBytes),
            nameof(AttachmentObjectGeneration.DetectedContentType),
            nameof(AttachmentObjectGeneration.CapturedAt));

        builder.HasIndex(generation => new
        {
            generation.Store,
            generation.Key,
            generation.Version,
        })
            .HasDatabaseName("ux_attachment_object_generation_version")
            .IsUnique();

        builder.HasIndex(generation => generation.AttachmentId)
            .HasDatabaseName("ix_attachment_object_generation_attachment");

        builder.HasOne<Attachment>()
            .WithMany()
            .HasForeignKey(generation => generation.AttachmentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.ToTable(table =>
        {
            table.HasCheckConstraint(
                "ck_attachment_object_generation_length_not_negative",
                "length_bytes >= 0");

            // A row that cannot name a generation is worse than a row that
            // cannot name a length: a blank generation reaching removal makes
            // the store place a delete marker instead of removing anything,
            // and that marker is what reopens the conditional write for a
            // second durable generation under the same key.
            table.HasCheckConstraint(
                "ck_attachment_object_generation_version_not_blank",
                "btrim(version) <> ''");
        });
    }

    private static void Freeze(
        EntityTypeBuilder<AttachmentObjectGeneration> builder,
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
