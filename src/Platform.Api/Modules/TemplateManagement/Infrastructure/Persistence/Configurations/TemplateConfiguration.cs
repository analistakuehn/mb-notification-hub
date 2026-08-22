using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence.Configurations;

internal sealed class TemplateConfiguration : IEntityTypeConfiguration<Template>
{
    public void Configure(EntityTypeBuilder<Template> builder)
    {
        builder.ToTable("template");

        // The key value object is exposed to the domain but persisted as the raw
        // string so ordering and keyset pagination translate to SQL directly.
        builder.Ignore(template => template.Key);
        builder.Property<string>(EntityKeyQueries.TemplateKeyProperty)
            .HasColumnName("key")
            .HasMaxLength(TemplateKey.MaxLength);
        builder.HasKey(EntityKeyQueries.TemplateKeyProperty);

        builder.Property(template => template.Application)
            .HasColumnName("application")
            .HasMaxLength(Template.MaxApplicationLength);

        builder.Property(template => template.Class)
            .HasColumnName("class")
            .HasMaxLength(20)
            .HasConversion(
                value => value.Canonical(),
                value => NotificationClasses.Trusted(value));

        builder.Property(template => template.OwnerTeam)
            .HasColumnName("owner_team")
            .HasMaxLength(Template.MaxTextLength);

        builder.Property(template => template.Purpose)
            .HasColumnName("purpose")
            .HasMaxLength(Template.MaxTextLength);

        builder.Property(template => template.LegalBasis)
            .HasColumnName("legal_basis")
            .HasMaxLength(Template.MaxTextLength);

        builder.Property(template => template.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion(
                value => value.Canonical(),
                value => TemplateStatuses.Trusted(value));

        builder.HasIndex(nameof(Template.Application), nameof(Template.Class))
            .HasDatabaseName("ix_template_application_class");
    }
}
