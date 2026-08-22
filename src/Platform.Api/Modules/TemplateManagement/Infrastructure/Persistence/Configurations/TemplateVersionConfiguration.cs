using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence.Configurations;

internal sealed class TemplateVersionConfiguration : IEntityTypeConfiguration<TemplateVersion>
{
    public void Configure(EntityTypeBuilder<TemplateVersion> builder)
    {
        builder.ToTable("template_version");

        builder.Ignore(version => version.TemplateKey);
        builder.Property<string>(EntityKeyQueries.VersionTemplateKeyProperty)
            .HasColumnName("template_key")
            .HasMaxLength(TemplateKey.MaxLength);

        builder.Property(version => version.Version)
            .HasColumnName("version");

        builder.HasKey(EntityKeyQueries.VersionTemplateKeyProperty, nameof(TemplateVersion.Version));

        builder.HasOne<Template>()
            .WithMany()
            .HasForeignKey(EntityKeyQueries.VersionTemplateKeyProperty)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(version => version.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion(
                value => value.Canonical(),
                value => TemplateVersionStatuses.Trusted(value));

        builder.Property(version => version.VariablesSchemaJson)
            .HasColumnName("variables_schema")
            .HasColumnType("jsonb");

        builder.Property(version => version.LayoutKey)
            .HasColumnName("layout_key")
            .HasMaxLength(LayoutKey.MaxLength);

        builder.Property(version => version.LayoutVersion)
            .HasColumnName("layout_version");

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

        // Database backstop for the one-open-draft-per-template invariant.
        builder.HasIndex(EntityKeyQueries.VersionTemplateKeyProperty)
            .IsUnique()
            .HasFilter("status = 'draft'")
            .HasDatabaseName("ux_template_version_single_draft");

        builder.OwnsMany(version => version.Contents, content =>
        {
            content.ToTable("template_content");

            content.WithOwner()
                .HasForeignKey("TemplateKey", "Version");
            content.Property<string>("TemplateKey")
                .HasColumnName("template_key")
                .HasMaxLength(TemplateKey.MaxLength);
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

            content.HasKey("TemplateKey", "Version", nameof(TemplateContent.Channel), nameof(TemplateContent.Locale));

            content.Property(entry => entry.Subject)
                .HasColumnName("subject")
                .HasMaxLength(TemplateVersion.MaxSubjectLength);

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
