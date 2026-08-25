using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class ProviderEventDedupeConfiguration : IEntityTypeConfiguration<ProviderEventDedupe>
{
    public void Configure(EntityTypeBuilder<ProviderEventDedupe> builder)
    {
        builder.ToTable("provider_event_dedupe");

        // Outside the partitioning exactly so this key can exist over the two
        // columns that carry the identity, without a time column joining it.
        builder.HasKey(mark => new { mark.Provider, mark.ProviderEventId });

        builder.Property(mark => mark.Provider)
            .HasColumnName("provider")
            .HasMaxLength(50);

        builder.Property(mark => mark.ProviderEventId)
            .HasColumnName("provider_event_id")
            .HasMaxLength(200);

        builder.Property(mark => mark.ProcessedAt)
            .HasColumnName("processed_at");

        // The purge reads by age alone; without this index it degrades into a
        // full scan of the busiest table of the tracker.
        builder.HasIndex(mark => mark.ProcessedAt)
            .HasDatabaseName("ix_provider_event_dedupe_processed");
    }
}
