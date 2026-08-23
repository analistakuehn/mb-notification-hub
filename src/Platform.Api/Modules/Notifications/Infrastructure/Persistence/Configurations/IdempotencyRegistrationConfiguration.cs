using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class IdempotencyRegistrationConfiguration : IEntityTypeConfiguration<IdempotencyRegistration>
{
    public void Configure(EntityTypeBuilder<IdempotencyRegistration> builder)
    {
        builder.ToTable("idempotency_key");

        builder.Property(registration => registration.Application)
            .HasColumnName("application")
            .HasMaxLength(100);

        builder.Property(registration => registration.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(200);

        // The scope of the idempotency contract is the natural key; being the
        // primary key it is also the unique constraint the ingestion relies on
        // to resolve concurrent replays.
        builder.HasKey(registration => new { registration.Application, registration.IdempotencyKey });

        builder.Property(registration => registration.PayloadHash)
            .HasColumnName("payload_hash")
            .HasMaxLength(64);

        builder.Property(registration => registration.NotificationId)
            .HasColumnName("notification_id");

        builder.Property(registration => registration.CreatedAt)
            .HasColumnName("created_at");

        // The purge job removes registrations past the contract window by age.
        builder.HasIndex(nameof(IdempotencyRegistration.CreatedAt))
            .HasDatabaseName("ix_idempotency_key_created_at");
    }
}
