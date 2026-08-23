using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence.Configurations;

internal sealed class LayoutConfiguration : IEntityTypeConfiguration<Layout>
{
    public void Configure(EntityTypeBuilder<Layout> builder)
    {
        builder.ToTable("layout");

        // The key value object is exposed to the domain but persisted as the raw
        // string so ordering and keyset pagination translate to SQL directly.
        builder.Ignore(layout => layout.Key);
        builder.Property<string>(EntityKeyQueries.LayoutKeyProperty)
            .HasColumnName("key")
            .HasMaxLength(LayoutKey.MaxLength);
        builder.HasKey(EntityKeyQueries.LayoutKeyProperty);

        builder.Property(layout => layout.OwnerTeam)
            .HasColumnName("owner_team")
            .HasMaxLength(Layout.MaxTextLength);

        builder.Property(layout => layout.DefaultLocale)
            .HasColumnName("default_locale")
            .HasMaxLength(5)
            .HasConversion(
                value => value!.Value,
                value => Locale.Trusted(value));

        builder.Property(layout => layout.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion(
                value => value.Canonical(),
                value => LayoutStatuses.Trusted(value));

        // Optimistic concurrency over the identity row through the xmin
        // system column: publish and rollback touch the layout in their
        // transaction, so a concurrent lifecycle transition (deprecate or
        // disable) surfaces as a concurrency conflict instead of publishing
        // under a status that no longer accepts it.
        builder.Property<uint>("xmin")
            .IsRowVersion();
    }
}
