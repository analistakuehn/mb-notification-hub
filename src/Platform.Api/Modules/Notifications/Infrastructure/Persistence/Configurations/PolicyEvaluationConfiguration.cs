using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class PolicyEvaluationConfiguration : IEntityTypeConfiguration<PolicyEvaluation>
{
    public void Configure(EntityTypeBuilder<PolicyEvaluation> builder)
    {
        builder.ToTable("policy_evaluation");

        builder.Property(evaluation => evaluation.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // Partitioned by month on evaluated_at, so the key is composite; the
        // surrogate id keeps each rule decision addressable.
        builder.HasKey(evaluation => new { evaluation.Id, evaluation.EvaluatedAt });

        builder.Property(evaluation => evaluation.NotificationId)
            .HasColumnName("notification_id");

        builder.Property(evaluation => evaluation.Rule)
            .HasColumnName("rule")
            .HasMaxLength(50);

        builder.Property(evaluation => evaluation.Result)
            .HasColumnName("result")
            .HasMaxLength(20);

        builder.Property(evaluation => evaluation.Reason)
            .HasColumnName("reason")
            .HasMaxLength(100);

        builder.Property(evaluation => evaluation.EvidenceJson)
            .HasColumnName("evidence")
            .HasColumnType("jsonb");

        builder.Property(evaluation => evaluation.EvaluatedAt)
            .HasColumnName("evaluated_at");

        builder.HasIndex(evaluation => evaluation.NotificationId)
            .HasDatabaseName("ix_policy_evaluation_notification");
    }
}
