using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Persistence.Configurations;

internal sealed class ApprovalConfiguration : IEntityTypeConfiguration<Approval>
{
    public void Configure(EntityTypeBuilder<Approval> builder)
    {
        builder.ToTable("approval");

        builder.Property(approval => approval.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(approval => approval.Id);

        builder.Property(approval => approval.SubjectType)
            .HasColumnName("subject_type")
            .HasMaxLength(30);

        // Wide enough for the longest subject naming of any producing context
        // (template and layout keys cap at 200 characters).
        builder.Property(approval => approval.SubjectId)
            .HasColumnName("subject_id")
            .HasMaxLength(200);

        builder.Property(approval => approval.SubjectVersion)
            .HasColumnName("subject_version");

        builder.Property(approval => approval.ContentHash)
            .HasColumnName("content_hash")
            .HasMaxLength(64);

        builder.Property(approval => approval.Role)
            .HasColumnName("role")
            .HasMaxLength(50);

        builder.Property(approval => approval.ApproverOid)
            .HasColumnName("approver_oid")
            .HasMaxLength(200);

        builder.Property(approval => approval.ApprovedAt)
            .HasColumnName("approved_at");

        builder.HasIndex(
                nameof(Approval.SubjectType),
                nameof(Approval.SubjectId),
                nameof(Approval.SubjectVersion))
            .HasDatabaseName("ix_approval_subject");
    }
}
