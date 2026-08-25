using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class DeliveryPayloadConfiguration : IEntityTypeConfiguration<DeliveryPayload>
{
    public void Configure(EntityTypeBuilder<DeliveryPayload> builder)
    {
        builder.ToTable("delivery_payload");

        builder.Property(payload => payload.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // Partitioned by month on received_at, in step with delivery_event;
        // PostgreSQL requires the partition column inside the primary key.
        builder.HasKey(payload => new { payload.Id, payload.ReceivedAt });

        builder.Property(payload => payload.ReceivedAt)
            .HasColumnName("received_at");

        builder.Property(payload => payload.ProviderKey)
            .HasColumnName("provider_key")
            .HasMaxLength(50);

        builder.Property(payload => payload.Source)
            .HasColumnName("source")
            .HasMaxLength(20);

        builder.Property(payload => payload.PayloadEncrypted)
            .HasColumnName("payload_enc");
    }
}
