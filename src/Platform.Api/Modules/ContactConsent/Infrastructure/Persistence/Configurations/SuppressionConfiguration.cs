using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence.Configurations;

internal sealed class SuppressionSignalRecordConfiguration : IEntityTypeConfiguration<SuppressionSignalRecord>
{
    public void Configure(EntityTypeBuilder<SuppressionSignalRecord> builder)
    {
        builder.ToTable("suppression_signal");

        builder.HasKey(signal => signal.Id);

        builder.Property(signal => signal.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(signal => signal.ContactPointId)
            .HasColumnName("contact_point_id");

        builder.Property(signal => signal.Channel)
            .HasColumnName("channel")
            .HasMaxLength(20);

        builder.Property(signal => signal.Reason)
            .HasColumnName("reason")
            .HasMaxLength(100);

        builder.Property(signal => signal.SourceEventId)
            .HasColumnName("source_event_id");

        builder.Property(signal => signal.ObservedAt)
            .HasColumnName("observed_at");

        builder.HasOne<ContactPoint>()
            .WithMany()
            .HasForeignKey(signal => signal.ContactPointId)
            .OnDelete(DeleteBehavior.Restrict);

        // The whole idempotency of the delivery-feedback path: a redelivery of
        // the internal message carries the same evidence row, collides here and
        // settles as a declarative no-op instead of inflating the count the
        // accumulation rule reads.
        builder.HasIndex(signal => signal.SourceEventId)
            .HasDatabaseName("ux_suppression_signal_source_event")
            .IsUnique();

        // The accumulation rule reads the refusals of one contact point inside
        // a window, newest first.
        builder.HasIndex(nameof(SuppressionSignalRecord.ContactPointId), nameof(SuppressionSignalRecord.ObservedAt))
            .HasDatabaseName("ix_suppression_signal_contact_point_observed");
    }
}

internal sealed class ContactSuppressionConfiguration : IEntityTypeConfiguration<ContactSuppression>
{
    public void Configure(EntityTypeBuilder<ContactSuppression> builder)
    {
        builder.ToTable("suppression");

        builder.HasKey(suppression => suppression.Id);

        builder.Property(suppression => suppression.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(suppression => suppression.ContactPointId)
            .HasColumnName("contact_point_id");

        builder.Property(suppression => suppression.Channel)
            .HasColumnName("channel")
            .HasMaxLength(20);

        builder.Property(suppression => suppression.Reason)
            .HasColumnName("reason")
            .HasMaxLength(100);

        builder.Property(suppression => suppression.Source)
            .HasColumnName("source")
            .HasMaxLength(30);

        builder.Property(suppression => suppression.ActorType)
            .HasColumnName("actor_type")
            .HasMaxLength(20);

        builder.Property(suppression => suppression.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(100);

        builder.Property(suppression => suppression.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(suppression => suppression.Until)
            .HasColumnName("until");

        builder.Property(suppression => suppression.RemovedAt)
            .HasColumnName("removed_at");

        builder.Property(suppression => suppression.RemovedBy)
            .HasColumnName("removed_by")
            .HasMaxLength(100);

        builder.HasOne<ContactPoint>()
            .WithMany()
            .HasForeignKey(suppression => suppression.ContactPointId)
            .OnDelete(DeleteBehavior.Restrict);

        // At most one suppression in force per contact point, enforced by the
        // store: two reports that race would otherwise leave the reversal
        // taking back one of two rows and the point still unaddressable.
        // Removed rows stay, so the history of a reversed decision survives it.
        builder.HasIndex(suppression => suppression.ContactPointId)
            .HasDatabaseName("ux_suppression_contact_point_active")
            .IsUnique()
            .HasFilter("removed_at IS NULL");
    }
}
