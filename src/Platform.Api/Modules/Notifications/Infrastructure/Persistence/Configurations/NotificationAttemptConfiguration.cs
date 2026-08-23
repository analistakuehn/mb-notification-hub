using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class NotificationAttemptConfiguration : IEntityTypeConfiguration<NotificationAttempt>
{
    public void Configure(EntityTypeBuilder<NotificationAttempt> builder)
    {
        builder.ToTable("notification_attempt");

        builder.Property(attempt => attempt.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // The table is partitioned by month on created_at; PostgreSQL requires
        // the partition column inside the primary key, so the key is composite.
        builder.HasKey(attempt => new { attempt.Id, attempt.CreatedAt });

        builder.Property(attempt => attempt.NotificationId)
            .HasColumnName("notification_id");

        builder.Property(attempt => attempt.Sequence)
            .HasColumnName("sequence");

        builder.Property(attempt => attempt.Channel)
            .HasColumnName("channel")
            .HasMaxLength(20);

        builder.Property(attempt => attempt.ProviderKey)
            .HasColumnName("provider_key")
            .HasMaxLength(50);

        builder.Property(attempt => attempt.ContactPointId)
            .HasColumnName("contact_point_id");

        // Logical reference into the contact directory, same regime as
        // contact_point_id: no physical foreign key across module schemas.
        builder.Property(attempt => attempt.DeviceTokenId)
            .HasColumnName("device_token_id");

        builder.Property(attempt => attempt.ProviderMessageId)
            .HasColumnName("provider_message_id")
            .HasMaxLength(200);

        builder.Property(attempt => attempt.RenderedContentEncrypted)
            .HasColumnName("rendered_content_enc");

        builder.Property(attempt => attempt.ContentHashFull)
            .HasColumnName("content_hash_full")
            .HasMaxLength(64);

        builder.Property(attempt => attempt.ContentHashMasked)
            .HasColumnName("content_hash_masked")
            .HasMaxLength(64);

        builder.Property(attempt => attempt.Status)
            .HasColumnName("status")
            .HasMaxLength(30);

        builder.Property(attempt => attempt.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(100);

        builder.Property(attempt => attempt.FallbackDeadline)
            .HasColumnName("fallback_deadline");

        builder.Property(attempt => attempt.SentAt)
            .HasColumnName("sent_at");

        builder.Property(attempt => attempt.DeliveredAt)
            .HasColumnName("delivered_at");

        builder.Property(attempt => attempt.CreatedAt)
            .HasColumnName("created_at");

        builder.HasIndex(attempt => attempt.NotificationId)
            .HasDatabaseName("ix_notification_attempt_notification");

        // The tracker scans for overdue fallbacks; the filter keeps the index
        // to the attempts that still carry a deadline.
        builder.HasIndex(nameof(NotificationAttempt.Status), nameof(NotificationAttempt.FallbackDeadline))
            .HasDatabaseName("ix_notification_attempt_fallback")
            .HasFilter("fallback_deadline IS NOT NULL");
    }
}
