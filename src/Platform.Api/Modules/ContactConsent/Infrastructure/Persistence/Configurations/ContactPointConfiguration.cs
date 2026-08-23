using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence.Configurations;

internal sealed class ContactPointConfiguration : IEntityTypeConfiguration<ContactPoint>
{
    public void Configure(EntityTypeBuilder<ContactPoint> builder)
    {
        builder.ToTable("contact_point");

        builder.HasKey(point => point.Id);

        builder.Property(point => point.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(point => point.RecipientId)
            .HasColumnName("recipient_id")
            .HasMaxLength(100);

        builder.Property(point => point.Channel)
            .HasColumnName("channel")
            .HasMaxLength(20);

        builder.Property(point => point.ValueEncrypted)
            .HasColumnName("value_enc");

        builder.Property(point => point.ValueHash)
            .HasColumnName("value_hash")
            .HasMaxLength(64);

        builder.Property(point => point.Verified)
            .HasColumnName("verified");

        builder.Property(point => point.RemovedAt)
            .HasColumnName("removed_at");

        builder.HasOne<RecipientProfile>()
            .WithMany()
            .HasForeignKey(point => point.RecipientId)
            .OnDelete(DeleteBehavior.Restrict);

        // The same normalized value only ever exists once per recipient and
        // channel, active or removed: a re-declaration revives the row instead
        // of duplicating it, so the consent ledger keeps a single anchor.
        builder.HasIndex(
                nameof(ContactPoint.RecipientId),
                nameof(ContactPoint.Channel),
                nameof(ContactPoint.ValueHash))
            .HasDatabaseName("ux_contact_point_recipient_channel_value")
            .IsUnique();

        // Equality search across recipients (which recipient owns this value),
        // the read that the deterministic keyed hash exists to serve.
        builder.HasIndex(nameof(ContactPoint.ValueHash))
            .HasDatabaseName("ix_contact_point_value_hash");
    }
}
