using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class ProducerRegistrationConfiguration : IEntityTypeConfiguration<ProducerRegistration>
{
    public void Configure(EntityTypeBuilder<ProducerRegistration> builder)
    {
        builder.ToTable("producer_registry");

        builder.Property(registration => registration.Principal)
            .HasColumnName("principal")
            .HasMaxLength(200);

        builder.Property(registration => registration.Application)
            .HasColumnName("application")
            .HasMaxLength(100);

        builder.Property(registration => registration.Class)
            .HasColumnName("class")
            .HasMaxLength(20);

        builder.Property(registration => registration.UpdatedAt)
            .HasColumnName("updated_at");

        // The grant itself is the key: one row per principal, application and
        // class, so a materialization job upserts without an identity column.
        builder.HasKey(
            nameof(ProducerRegistration.Principal),
            nameof(ProducerRegistration.Application),
            nameof(ProducerRegistration.Class));
    }
}
