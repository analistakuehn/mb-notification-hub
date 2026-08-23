using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

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

        builder.Property(message => message.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(message => message.SentAt)
            .HasColumnName("sent_at");

        // The relay's read shape: pending rows only, one transport lane at a
        // time, grouped by priority class in arrival order. Transport leads
        // because every claim filters it first. Partial on purpose so the
        // index stays small once sent rows accumulate.
        builder.HasIndex(
                nameof(OutboxMessage.Transport),
                nameof(OutboxMessage.PriorityClass),
                nameof(OutboxMessage.CreatedAt))
            .HasDatabaseName("ix_outbox_pending")
            .HasFilter("sent_at IS NULL");
    }
}
