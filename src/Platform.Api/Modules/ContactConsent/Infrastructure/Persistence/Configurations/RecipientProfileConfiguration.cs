using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.Api.Modules.ContactConsent.Infrastructure.Persistence.Configurations;

internal sealed class RecipientProfileConfiguration : IEntityTypeConfiguration<RecipientProfile>
{
    public void Configure(EntityTypeBuilder<RecipientProfile> builder)
    {
        builder.ToTable("recipient_profile");

        builder.HasKey(profile => profile.RecipientId);

        builder.Property(profile => profile.RecipientId)
            .HasColumnName("recipient_id")
            .HasMaxLength(100);

        builder.Property(profile => profile.Timezone)
            .HasColumnName("timezone")
            .HasMaxLength(50);

        builder.Property(profile => profile.Locale)
            .HasColumnName("locale")
            .HasMaxLength(20);

        builder.Property(profile => profile.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(profile => profile.UpdatedAt)
            .HasColumnName("updated_at");
    }
}
