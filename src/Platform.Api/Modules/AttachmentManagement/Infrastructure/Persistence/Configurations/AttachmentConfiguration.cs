using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence.Configurations;

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachment");

        builder.Property(attachment => attachment.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(attachment => attachment.Id);

        builder.Property(attachment => attachment.Reference)
            .HasColumnName("reference")
            .HasConversion(
                reference => reference.Value,
                value => AttachmentReference.Trusted(value))
            .HasMaxLength(AttachmentReference.Length);

        builder.Property(attachment => attachment.Application)
            .HasColumnName("application")
            .HasMaxLength(Attachment.MaxApplicationLength);

        builder.Property(attachment => attachment.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(Attachment.MaxFileNameLength);

        builder.Property(attachment => attachment.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(Attachment.MaxContentTypeLength);

        builder.Property(attachment => attachment.SizeBytes)
            .HasColumnName("size_bytes");

        builder.Property(attachment => attachment.ContentId)
            .HasColumnName("content_id");

        builder.Property(attachment => attachment.State)
            .HasColumnName("state")
            .HasMaxLength(30);

        builder.Property(attachment => attachment.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(attachment => attachment.ReceivedAt)
            .HasColumnName("received_at");

        // The two columns the verdict leaves behind. They sit on the aggregate
        // and not on the release row because they describe an attachment that
        // was refused or is waiting, and neither of those has a release.
        builder.Property(attachment => attachment.ValidationDetail)
            .HasColumnName("validation_detail")
            .HasMaxLength(Attachment.MaxValidationDetailLength);

        builder.Property(attachment => attachment.InconclusiveUntil)
            .HasColumnName("inconclusive_until");

        builder.HasIndex(attachment => attachment.Reference)
            .HasDatabaseName("ux_attachment_reference")
            .IsUnique();

        builder.HasIndex(attachment => attachment.ContentId)
            .HasDatabaseName("ux_attachment_content_id")
            .IsUnique();

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_attachment_size_positive",
            "size_bytes > 0"));

        builder.Property<uint>("xmin")
            .IsRowVersion();
    }
}
