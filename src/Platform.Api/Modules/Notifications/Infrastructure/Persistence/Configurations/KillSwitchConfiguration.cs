using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Persistence.Configurations;

internal sealed class KillSwitchConfiguration : IEntityTypeConfiguration<KillSwitchState>
{
    public void Configure(EntityTypeBuilder<KillSwitchState> builder)
    {
        builder.ToTable("kill_switch", table =>
        {
            table.HasCheckConstraint(
                "ck_kill_switch_scope",
                "scope IN ('producer', 'application', 'channel')");
            table.HasCheckConstraint(
                "ck_kill_switch_state",
                "state IN ('active', 'inactive')");
            table.HasCheckConstraint("ck_kill_switch_version", "version > 0");
        });
        builder.HasKey(entry => new { entry.Scope, entry.Key });

        builder.Property(entry => entry.Scope)
            .HasColumnName("scope")
            .HasMaxLength(20);
        builder.Property(entry => entry.Key)
            .HasColumnName("key")
            .HasMaxLength(KillSwitchKeys.MaxLength);
        builder.Property(entry => entry.State)
            .HasColumnName("state")
            .HasMaxLength(10);
        builder.Property(entry => entry.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();
        builder.Property(entry => entry.Actor)
            .HasColumnName("actor")
            .HasMaxLength(200);
        builder.Property(entry => entry.SecondActor)
            .HasColumnName("second_actor")
            .HasMaxLength(200);
        builder.Property(entry => entry.UpdatedAt)
            .HasColumnName("updated_at");
        builder.Ignore(entry => entry.IsActive);
    }
}
