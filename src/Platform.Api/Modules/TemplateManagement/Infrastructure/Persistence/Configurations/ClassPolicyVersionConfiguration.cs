using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence.Configurations;

internal sealed class ClassPolicyVersionConfiguration : IEntityTypeConfiguration<ClassPolicyVersion>
{
    public void Configure(EntityTypeBuilder<ClassPolicyVersion> builder)
    {
        builder.ToTable("class_policy_version");

        builder.Property(version => version.Application)
            .HasColumnName("application")
            .HasMaxLength(ApplicationName.MaxLength);

        builder.Property(version => version.Class)
            .HasColumnName("class")
            .HasMaxLength(20)
            .HasConversion(
                value => value.Canonical(),
                value => NotificationClasses.Trusted(value));

        builder.Property(version => version.Version)
            .HasColumnName("version");

        builder.HasKey(
            nameof(ClassPolicyVersion.Application),
            nameof(ClassPolicyVersion.Class),
            nameof(ClassPolicyVersion.Version));

        builder.Property(version => version.Status)
            .HasColumnName("status")
            .HasMaxLength(20)
            .HasConversion(
                value => value.Canonical(),
                value => ClassPolicyVersionStatuses.Trusted(value));

        builder.Property(version => version.SchemaVersion)
            .HasColumnName("schema_version");

        // Plain text on purpose: the canonical hash covers the submitted
        // bytes, and jsonb would rewrite number literals, whitespace and key
        // order on round trip, breaking hash verification after a reload.
        builder.Property(version => version.DefinitionJson)
            .HasColumnName("definition");

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

        builder.PrimitiveCollection(version => version.Editors)
            .HasField("_editors")
            .HasColumnName("editors");

        builder.Property(version => version.EntityTag)
            .HasColumnName("etag")
            .HasMaxLength(64)
            .IsConcurrencyToken();

        // Database backstop for the one-open-draft-per-policy invariant.
        builder.HasIndex(nameof(ClassPolicyVersion.Application), nameof(ClassPolicyVersion.Class))
            .IsUnique()
            .HasFilter("status = 'draft'")
            .HasDatabaseName("ux_class_policy_version_single_draft");

        // Database backstop for the one-published-version-per-policy
        // invariant: concurrent publications surface as a unique violation
        // instead of two published versions.
        builder.HasIndex(
                [nameof(ClassPolicyVersion.Application), nameof(ClassPolicyVersion.Class)],
                "ux_class_policy_version_single_published")
            .IsUnique()
            .HasFilter("status = 'published'")
            .HasDatabaseName("ux_class_policy_version_single_published");
    }
}
