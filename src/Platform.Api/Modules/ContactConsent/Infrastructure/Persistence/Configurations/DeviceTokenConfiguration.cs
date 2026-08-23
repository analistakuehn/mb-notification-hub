using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence.Configurations;

internal sealed class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        builder.ToTable("device_token");

        builder.HasKey(device => device.Id);

        builder.Property(device => device.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(device => device.RecipientId)
            .HasColumnName("recipient_id")
            .HasMaxLength(100);

        builder.Property(device => device.Token)
            .HasColumnName("token")
            .HasMaxLength(512);

        builder.Property(device => device.Platform)
            .HasColumnName("platform")
            .HasMaxLength(20);

        builder.Property(device => device.AppVersion)
            .HasColumnName("app_version")
            .HasMaxLength(50);

        builder.Property(device => device.RegisteredAt)
            .HasColumnName("registered_at");

        builder.Property(device => device.LastSeenAt)
            .HasColumnName("last_seen_at");

        builder.Property(device => device.InvalidatedAt)
            .HasColumnName("invalidated_at");

        builder.HasOne<RecipientProfile>()
            .WithMany()
            .HasForeignKey(device => device.RecipientId)
            .OnDelete(DeleteBehavior.Restrict);

        // A re-registration of the same token by the same recipient refreshes
        // the existing row instead of duplicating it.
        builder.HasIndex(nameof(DeviceToken.RecipientId), nameof(DeviceToken.Token))
            .HasDatabaseName("ux_device_token_recipient_token")
            .IsUnique();

        // Active-token reads order the fan-out by recency.
        builder.HasIndex(nameof(DeviceToken.RecipientId), nameof(DeviceToken.LastSeenAt))
            .HasDatabaseName("ix_device_token_recipient_last_seen")
            .IsDescending(false, true);
    }
}
