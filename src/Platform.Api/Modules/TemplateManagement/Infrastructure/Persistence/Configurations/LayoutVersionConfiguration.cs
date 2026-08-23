using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence.Configurations;

internal sealed class LayoutVersionConfiguration : IEntityTypeConfiguration<LayoutVersion>
{
    public void Configure(EntityTypeBuilder<LayoutVersion> builder)
    {
        builder.ToTable("layout_version");

        builder.Ignore(version => version.LayoutKey);
        builder.Property<string>(EntityKeyQueries.VersionLayoutKeyProperty)
            .HasColumnName("layout_key")
            .HasMaxLength(LayoutKey.MaxLength);

        builder.Property(version => version.Version)
            .HasColumnName("version");

        builder.HasKey(EntityKeyQueries.VersionLayoutKeyProperty, nameof(LayoutVersion.Version));

        builder.HasOne<Layout>()
            .WithMany()
            .HasForeignKey(EntityKeyQueries.VersionLayoutKeyProperty)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(version => version.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion(
                value => value.Canonical(),
                value => LayoutVersionStatuses.Trusted(value));

        builder.Property(version => version.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(64);

        builder.Property(version => version.CreatedBy)
            .HasColumnName("created_by")
            .HasMaxLength(200);

        builder.Property(version => version.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(version => version.PublishedAt)
            .HasColumnName("published_at");

        builder.Property(version => version.RolledBackFrom)
            .HasColumnName("rolled_back_from");

        builder.PrimitiveCollection(version => version.Editors)
            .HasField("_editors")
            .HasColumnName("editors");

        builder.Property(version => version.EntityTag)
            .HasColumnName("etag")
            .HasMaxLength(64)
            .IsConcurrencyToken();

        // Database backstop for the one-open-draft-per-layout invariant.
        builder.HasIndex(EntityKeyQueries.VersionLayoutKeyProperty)
            .IsUnique()
            .HasFilter("status = 'draft'")
            .HasDatabaseName("ux_layout_version_single_draft");

        // Database backstop for the one-published-version-per-layout
        // invariant: concurrent publish/rollback races surface as a unique
        // violation instead of two published versions.
        builder.HasIndex([EntityKeyQueries.VersionLayoutKeyProperty], "ux_layout_version_single_published")
            .IsUnique()
            .HasFilter("status = 'published'")
            .HasDatabaseName("ux_layout_version_single_published");

        builder.OwnsMany(version => version.Contents, content =>
        {
            content.ToTable("layout_content");

            content.WithOwner()
                .HasForeignKey("LayoutKey", "Version");
            content.Property<string>("LayoutKey")
                .HasColumnName("layout_key")
                .HasMaxLength(LayoutKey.MaxLength);
            content.Property<int>("Version")
                .HasColumnName("version");

            content.Property(entry => entry.Channel)
                .HasColumnName("channel")
                .HasMaxLength(20)
                .HasConversion(
                    value => value.Value,
                    value => Channel.Trusted(value));

            content.Property(entry => entry.Locale)
                .HasColumnName("locale")
                .HasMaxLength(5)
                .HasConversion(
                    value => value.Value,
                    value => Locale.Trusted(value));

            content.HasKey("LayoutKey", "Version", nameof(LayoutContent.Channel), nameof(LayoutContent.Locale));

            content.Property(entry => entry.Body)
                .HasColumnName("body");

            content.Property(entry => entry.BodyText)
                .HasColumnName("body_text");

            content.Property(entry => entry.BodyHash)
                .HasColumnName("body_hash")
                .HasMaxLength(64);
        });

        builder.Navigation(version => version.Contents)
            .UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
