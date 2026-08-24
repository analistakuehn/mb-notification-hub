using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class KillSwitchHoldConfiguration : IEntityTypeConfiguration<KillSwitchHold>
{
    public void Configure(EntityTypeBuilder<KillSwitchHold> builder)
    {
        builder.ToTable("kill_switch_hold", table =>
        {
            table.HasCheckConstraint(
                "ck_kill_switch_hold_scope",
                "scope IN ('producer', 'application', 'channel')");
            table.HasCheckConstraint(
                "ck_kill_switch_hold_work_kind",
                "work_kind IN ('core', 'fallback', 'dispatch')");
            table.HasCheckConstraint("ck_kill_switch_hold_version", "version > 0");
        });
        builder.HasKey(hold => hold.Id);

        builder.Property(hold => hold.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(hold => hold.WorkKind)
            .HasColumnName("work_kind")
            .HasMaxLength(20);
        builder.Property(hold => hold.WorkId)
            .HasColumnName("work_id")
            .HasMaxLength(200);
        builder.Property(hold => hold.Scope)
            .HasColumnName("scope")
            .HasMaxLength(20);
        builder.Property(hold => hold.Key)
            .HasColumnName("key")
            .HasMaxLength(KillSwitchKeys.MaxLength);
        builder.Property(hold => hold.Destination)
            .HasColumnName("destination")
            .HasMaxLength(200);
        builder.Property(hold => hold.PayloadJson)
            .HasColumnName("payload")
            .HasColumnType("jsonb");
        builder.Property(hold => hold.ExpiresAt).HasColumnName("expires_at");
        builder.Property(hold => hold.ReleasedAt).HasColumnName("released_at");
        builder.Property(hold => hold.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasIndex(hold => new { hold.WorkKind, hold.WorkId })
            .IsUnique()
            .HasDatabaseName("ux_kill_switch_hold_work");
        builder.HasIndex(hold => new { hold.ExpiresAt, hold.Id })
            .HasFilter("released_at IS NULL")
            .HasDatabaseName("ix_kill_switch_hold_unreleased");
    }
}
