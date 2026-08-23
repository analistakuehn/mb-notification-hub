using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence.Configurations;

internal sealed class ConsentConfiguration : IEntityTypeConfiguration<Consent>
{
    public void Configure(EntityTypeBuilder<Consent> builder)
    {
        builder.ToTable("consent");

        builder.HasKey(consent => consent.Id);

        builder.Property(consent => consent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(consent => consent.ContactPointId)
            .HasColumnName("contact_point_id");

        builder.Property(consent => consent.Purpose)
            .HasColumnName("purpose")
            .HasMaxLength(100);

        builder.Property(consent => consent.Granted)
            .HasColumnName("granted");

        builder.Property(consent => consent.Source)
            .HasColumnName("source")
            .HasMaxLength(20);

        builder.Property(consent => consent.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(200);

        builder.Property(consent => consent.TermsVersion)
            .HasColumnName("terms_version")
            .HasMaxLength(50);

        builder.Property(consent => consent.RecordedAt)
            .HasColumnName("recorded_at");

        builder.HasOne<ContactPoint>()
            .WithMany()
            .HasForeignKey(consent => consent.ContactPointId)
            .OnDelete(DeleteBehavior.Restrict);

        // The current state of a (purpose, channel) pair is the latest record
        // reached through the recipient's contact points; this index serves
        // that read without scanning the whole ledger.
        builder.HasIndex(
                nameof(Consent.ContactPointId),
                nameof(Consent.Purpose),
                nameof(Consent.RecordedAt))
            .HasDatabaseName("ix_consent_point_purpose_recorded")
            .IsDescending(false, false, true);
    }
}
