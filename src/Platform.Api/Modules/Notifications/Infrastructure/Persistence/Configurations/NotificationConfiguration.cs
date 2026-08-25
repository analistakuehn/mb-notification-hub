using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notification");

        builder.Property(notification => notification.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // The table is partitioned by month on created_at; PostgreSQL requires
        // the partition column inside the primary key, so the key is composite.
        // The id stays globally unique in practice (UUID v7 generated in code).
        builder.HasKey(notification => new { notification.Id, notification.CreatedAt });

        builder.Property(notification => notification.Application)
            .HasColumnName("application")
            .HasMaxLength(100);

        builder.Property(notification => notification.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(200);

        builder.Property(notification => notification.RecipientId)
            .HasColumnName("recipient_id")
            .HasMaxLength(100);

        builder.Property(notification => notification.Class)
            .HasColumnName("class")
            .HasMaxLength(20);

        builder.Property(notification => notification.TemplateKey)
            .HasColumnName("template_key")
            .HasMaxLength(200);

        builder.Property(notification => notification.TemplateVersion)
            .HasColumnName("template_version");

        // Written false by default so a row created before this column existed
        // reads exactly like a notification outside an authentication flow.
        builder.Property(notification => notification.AuthFlow)
            .HasColumnName("auth_flow")
            .HasDefaultValue(false);

        builder.Property(notification => notification.PolicyVersion)
            .HasColumnName("policy_version");

        builder.Property(notification => notification.AdmittedPlanJson)
            .HasColumnName("admitted_plan")
            .HasColumnType("jsonb");

        builder.Property(notification => notification.VariablesMaskedJson)
            .HasColumnName("variables_masked")
            .HasColumnType("jsonb");

        builder.Property(notification => notification.VariablesEncrypted)
            .HasColumnName("variables_enc");

        builder.Property(notification => notification.CorrelationId)
            .HasColumnName("correlation_id")
            .HasMaxLength(200);

        builder.Property(notification => notification.RequestedBy)
            .HasColumnName("requested_by")
            .HasMaxLength(200);

        builder.Property(notification => notification.Status)
            .HasColumnName("status")
            .HasMaxLength(30);

        builder.Property(notification => notification.ReleaseAt)
            .HasColumnName("release_at");

        builder.Property(notification => notification.ExpiresAt)
            .HasColumnName("expires_at");

        builder.Property(notification => notification.CreatedAt)
            .HasColumnName("created_at");

        builder.HasIndex(nameof(Notification.RecipientId), nameof(Notification.CreatedAt))
            .HasDatabaseName("ix_notification_recipient")
            .IsDescending(false, true);

        builder.HasIndex(nameof(Notification.CorrelationId))
            .HasDatabaseName("ix_notification_correlation");

        // The release scan reads deferred notifications, so the deferred state
        // is what the filter names, not the mere presence of a release instant.
        // The distinction is the whole point: a released notification keeps its
        // instant as evidence of why it waited, and an index filtered on the
        // instant would hold every notification that was ever deferred, growing
        // without bound while the scan is only ever interested in the ones
        // still waiting.
        builder.HasIndex(nameof(Notification.ReleaseAt))
            .HasDatabaseName("ix_notification_release_due")
            .HasFilter("status = 'deferred'");
    }
}
