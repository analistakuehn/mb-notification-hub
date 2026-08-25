using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class DeliveryEventConfiguration : IEntityTypeConfiguration<DeliveryEvent>
{
    public void Configure(EntityTypeBuilder<DeliveryEvent> builder)
    {
        builder.ToTable("delivery_event");

        builder.Property(deliveryEvent => deliveryEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // The table is partitioned by month on received_at, the instant this
        // hub took the callback; PostgreSQL requires the partition column
        // inside the primary key, so the key is composite.
        builder.HasKey(deliveryEvent => new { deliveryEvent.Id, deliveryEvent.ReceivedAt });

        builder.Property(deliveryEvent => deliveryEvent.ReceivedAt)
            .HasColumnName("received_at");

        // Logical references, not physical foreign keys: the row exists before
        // correlation resolves anything, and a callback that arrives before
        // the send transaction commits must still be stored.
        builder.Property(deliveryEvent => deliveryEvent.AttemptId)
            .HasColumnName("attempt_id");

        builder.Property(deliveryEvent => deliveryEvent.NotificationId)
            .HasColumnName("notification_id");

        builder.Property(deliveryEvent => deliveryEvent.ProviderKey)
            .HasColumnName("provider_key")
            .HasMaxLength(50);

        builder.Property(deliveryEvent => deliveryEvent.ProviderEventId)
            .HasColumnName("provider_event_id")
            .HasMaxLength(200);

        builder.Property(deliveryEvent => deliveryEvent.ProviderMessageId)
            .HasColumnName("provider_message_id")
            .HasMaxLength(200);

        builder.Property(deliveryEvent => deliveryEvent.Kind)
            .HasColumnName("kind")
            .HasMaxLength(20);

        builder.Property(deliveryEvent => deliveryEvent.OccurredAt)
            .HasColumnName("occurred_at");

        builder.Property(deliveryEvent => deliveryEvent.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(100);

        builder.Property(deliveryEvent => deliveryEvent.SuppressionSignal)
            .HasColumnName("suppression_signal")
            .HasMaxLength(30);

        builder.Property(deliveryEvent => deliveryEvent.PayloadId)
            .HasColumnName("payload_id");

        builder.Property(deliveryEvent => deliveryEvent.AppliedAt)
            .HasColumnName("applied_at");

        builder.Property(deliveryEvent => deliveryEvent.SuppressionReportedAt)
            .HasColumnName("suppression_reported_at");

        builder.HasIndex(deliveryEvent => deliveryEvent.NotificationId)
            .HasDatabaseName("ix_delivery_event_notification")
            .HasFilter("notification_id IS NOT NULL");

        builder.HasIndex(deliveryEvent => deliveryEvent.AttemptId)
            .HasDatabaseName("ix_delivery_event_attempt")
            .HasFilter("attempt_id IS NOT NULL");

        // The drain of suppression reports this hub still owes. The predicate
        // is written literally, and all three conjuncts belong to it: applied
        // rows carrying a real signal and no report stamp are a set that is
        // empty almost always, and the index has to be small enough that a
        // scan every few seconds costs nothing to prove it.
        builder.HasIndex(deliveryEvent => deliveryEvent.ReceivedAt)
            .HasDatabaseName("ix_delivery_event_suppression_pending")
            .HasFilter(
                "suppression_signal <> 'none' AND applied_at IS NOT NULL "
                + "AND suppression_reported_at IS NULL");
    }
}
