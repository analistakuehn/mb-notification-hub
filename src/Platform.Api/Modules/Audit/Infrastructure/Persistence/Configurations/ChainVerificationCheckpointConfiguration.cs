using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Audit.Infrastructure.Verification;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Persistence.Configurations;

internal sealed class ChainVerificationCheckpointConfiguration
    : IEntityTypeConfiguration<ChainVerificationCheckpoint>
{
    public void Configure(EntityTypeBuilder<ChainVerificationCheckpoint> builder)
    {
        // Deliberately outside the append-only regime of the trail: this table
        // records how far a job got, and a job that cannot update its own
        // progress would replay the whole history on every round.
        builder.ToTable("chain_verification_checkpoint");

        builder.HasKey(checkpoint => checkpoint.PartitionName);

        builder.Property(checkpoint => checkpoint.PartitionName)
            .HasColumnName("partition_name")
            .HasMaxLength(63);

        builder.Property(checkpoint => checkpoint.LastSeq)
            .HasColumnName("last_seq");

        builder.Property(checkpoint => checkpoint.LastHash)
            .HasColumnName("last_hash");

        builder.Property(checkpoint => checkpoint.VerifiedAt)
            .HasColumnName("verified_at");

        builder.Property(checkpoint => checkpoint.FullyVerifiedAt)
            .HasColumnName("fully_verified_at");

        builder.Property(checkpoint => checkpoint.Failure)
            .HasColumnName("failure")
            .HasMaxLength(100);

        builder.Property(checkpoint => checkpoint.FailedSeq)
            .HasColumnName("failed_seq");
    }
}
