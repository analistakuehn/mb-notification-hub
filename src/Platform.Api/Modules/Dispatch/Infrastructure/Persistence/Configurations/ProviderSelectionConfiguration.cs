using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Dispatch.Domain;

namespace NotificationHub.Api.Modules.Dispatch.Infrastructure.Persistence.Configurations;

internal sealed class ProviderSelectionConfiguration : IEntityTypeConfiguration<ProviderSelection>
{
    public void Configure(EntityTypeBuilder<ProviderSelection> builder)
    {
        builder.ToTable("provider_config");

        builder.Property(selection => selection.ChannelValue)
            .HasColumnName("channel")
            .HasMaxLength(20);

        builder.Property(selection => selection.ProviderKey)
            .HasColumnName("provider_key")
            .HasMaxLength(50);

        builder.Property(selection => selection.Priority)
            .HasColumnName("priority");

        builder.Property(selection => selection.UpdatedAt)
            .HasColumnName("updated_at");

        // Composite key on purpose: one channel may list several providers,
        // ordered by priority, so future failover is a data change only.
        builder.HasKey(
            nameof(ProviderSelection.ChannelValue),
            nameof(ProviderSelection.ProviderKey));
    }
}
