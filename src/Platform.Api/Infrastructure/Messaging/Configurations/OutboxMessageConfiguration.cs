using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.Api.Infrastructure.Messaging.Configurations;

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox");

        builder.Property(message => message.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(message => message.Id);

        builder.Property(message => message.Destination)
            .HasColumnName("destination")
            .HasMaxLength(100);

        builder.Property(message => message.Transport)
            .HasColumnName("transport")
            .HasMaxLength(20)
            .HasDefaultValue(OutboxTransports.Sqs);

        builder.Property(message => message.EventType)
            .HasColumnName("event_type")
            .HasMaxLength(100);

        builder.Property(message => message.MessageKey)
            .HasColumnName("message_key")
            .HasMaxLength(200);

        builder.Property(message => message.HeadersJson)
            .HasColumnName("headers")
            .HasColumnType("jsonb");

        builder.Property(message => message.PayloadJson)
            .HasColumnName("payload")
            .HasColumnType("jsonb");

        builder.Property(message => message.PriorityClass)
            .HasColumnName("priority_class")
            .HasMaxLength(20);

        // The band the relay claims by, stored instead of derived: the reader
        // used to spell the same CASE inside its predicate, and an expression
        // in the predicate is what left every claim without an index. The
        // database computes it on write from two values the producer already
        // supplies, and GENERATED ALWAYS refuses any writer that tries to
        // supply it, so no insert path can leave the row outside its band.
        builder.Property(message => message.PriorityBand)
            .HasColumnName("priority_band")
            .HasComputedColumnSql(OutboxBands.ClassificationSql, stored: true);

        builder.Property(message => message.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(message => message.SentAt)
            .HasColumnName("sent_at");

        // The relay's read shape: pending rows only, one transport lane at a
        // time, one band at a time, in arrival order. Transport leads because
        // every claim filters it first, the band follows because it is the
        // second equality, and created_at closes the index so the batch comes
        // out ordered without a sort. Partial on purpose so the index stays
        // small once sent rows accumulate; the claim carries the same
        // predicate literally, which is what lets the planner match it.
        builder.HasIndex(
                nameof(OutboxMessage.Transport),
                nameof(OutboxMessage.PriorityBand),
                nameof(OutboxMessage.CreatedAt))
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("sent_at IS NULL");
    }
}
