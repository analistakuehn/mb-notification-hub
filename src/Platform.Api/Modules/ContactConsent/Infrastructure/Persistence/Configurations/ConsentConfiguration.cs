using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;

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
            .HasMaxLength(ConsentPurpose.MaxLength);

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
        // that read without scanning the whole ledger. It stays a plain btree
        // over the stored column: the aggregate writes the purpose canonical,
        // and no read predicates on it. Every resolution scans the records of
        // the recipient's own contact points and groups them in memory on the
        // canonical key, which is what folds the rows written before the
        // aggregate canonicalized into a single lineage.
        builder.HasIndex(
                nameof(Consent.ContactPointId),
                nameof(Consent.Purpose),
                nameof(Consent.RecordedAt))
            .HasDatabaseName("ix_consent_point_purpose_recorded")
            .IsDescending(false, false, true);
    }
}
