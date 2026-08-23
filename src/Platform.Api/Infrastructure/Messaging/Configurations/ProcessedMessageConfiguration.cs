using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace NotificationHub.Api.Infrastructure.Messaging.Configurations;

internal sealed class ProcessedMessageConfiguration : IEntityTypeConfiguration<ProcessedMessage>
{
    public void Configure(EntityTypeBuilder<ProcessedMessage> builder)
    {
        builder.ToTable("processed_messages");

        builder.Property(message => message.MessageId)
            .HasColumnName("message_id")
            .HasMaxLength(200);

        builder.Property(message => message.Consumer)
            .HasColumnName("consumer")
            .HasMaxLength(100);

        builder.Property(message => message.ProcessedAt)
            .HasColumnName("processed_at");

        builder.HasKey(message => new { message.MessageId, message.Consumer });

        // The purge job deletes by age; without this index every round scans.
        builder.HasIndex(message => message.ProcessedAt)
            .HasDatabaseName("ix_processed_messages_processed_at");
    }
}
