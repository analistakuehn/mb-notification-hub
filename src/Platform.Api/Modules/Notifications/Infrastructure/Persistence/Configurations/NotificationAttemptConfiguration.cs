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

        builder.Property(attempt => attempt.PlanAdvancedAt)
            .HasColumnName("plan_advanced_at");

        builder.Property(attempt => attempt.StatusChangedAt)
            .HasColumnName("status_changed_at");

        builder.Property(attempt => attempt.FallbackRequestedAt)
            .HasColumnName("fallback_requested_at");

        builder.Property(attempt => attempt.SentAt)
            .HasColumnName("sent_at");

        builder.Property(attempt => attempt.DeliveredAt)
            .HasColumnName("delivered_at");

        builder.Property(attempt => attempt.CreatedAt)
            .HasColumnName("created_at");

        builder.HasIndex(attempt => attempt.NotificationId)
            .HasDatabaseName("ix_notification_attempt_notification");

        // The scheduler scans for overdue fallbacks. The filter keeps the index
        // to the attempts that can still produce one: a deadline is stamped,
        // the step has not advanced, and no trigger is in flight for this row.
        // All three conjuncts earn their place by removing rows the scan can
        // never act on, so an attempt whose step moved and an attempt whose
        // trigger is already on the queue both leave the index instead of
        // being read and discarded once every round for the rest of the
        // partition's life.
        builder.HasIndex(nameof(NotificationAttempt.Status), nameof(NotificationAttempt.FallbackDeadline))
            .HasDatabaseName("ix_notification_attempt_fallback_due")
            .HasFilter(
                "fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL "
                + "AND fallback_requested_at IS NULL");

        // The same scan, asking a different question: an attempt parked on an
        // inconclusive verdict for longer than the grace period. Age is the
        // ordering key here, because the predicate on the status is an equality
        // the filter already carries.
        builder.HasIndex(nameof(NotificationAttempt.StatusChangedAt))
            .HasDatabaseName("ix_notification_attempt_unknown_due")
            .HasFilter(
                "status = 'unknown' AND fallback_deadline IS NOT NULL "
                + "AND plan_advanced_at IS NULL AND fallback_requested_at IS NULL");

        // The complement of the two above: the triggers currently in flight.
        // The round that ages a stale request out reads only this index, and
        // it is small by construction, because a request leaves it as soon as
        // the handler claims the step it asked for.
        builder.HasIndex(nameof(NotificationAttempt.FallbackRequestedAt))
            .HasDatabaseName("ix_notification_attempt_fallback_inflight")
            .HasFilter(
                "fallback_deadline IS NOT NULL AND plan_advanced_at IS NULL "
                + "AND fallback_requested_at IS NOT NULL");

        // Delivery feedback that echoes no correlation is joined back to its
        // attempt by the provider's own message identity. Only a claimed
        // attempt carries one, so the filter keeps the index to the fraction
        // of rows the lookup can ever match.
        builder.HasIndex(attempt => attempt.ProviderMessageId)
            .HasDatabaseName("ix_notification_attempt_provider_message")
            .HasFilter("provider_message_id IS NOT NULL");
    }
}
