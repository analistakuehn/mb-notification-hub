using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        builder.ToTable("audit_event");

        builder.Property(auditEvent => auditEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(auditEvent => auditEvent.Id);

        builder.Property(auditEvent => auditEvent.Seq)
            .HasColumnName("seq")
            .UseSerialColumn();

        builder.Property(auditEvent => auditEvent.OccurredAt)
            .HasColumnName("occurred_at");

        builder.Property(auditEvent => auditEvent.ActorType)
            .HasColumnName("actor_type")
            .HasMaxLength(20);

        builder.Property(auditEvent => auditEvent.ActorId)
            .HasColumnName("actor_id")
            .HasMaxLength(200);

        builder.Property(auditEvent => auditEvent.Application)
            .HasColumnName("application")
            .HasMaxLength(100);

        builder.Property(auditEvent => auditEvent.Action)
            .HasColumnName("action")
            .HasMaxLength(100);

        builder.Property(auditEvent => auditEvent.EntityType)
            .HasColumnName("entity_type")
            .HasMaxLength(30);

        builder.Property(auditEvent => auditEvent.EntityId)
            .HasColumnName("entity_id")
            .HasMaxLength(250);

        builder.Property(auditEvent => auditEvent.DetailsJson)
            .HasColumnName("details")
            .HasColumnType("jsonb");

        builder.HasIndex(nameof(AuditEvent.EntityType), nameof(AuditEvent.EntityId))
            .HasDatabaseName("ix_audit_event_entity");
    }
}
