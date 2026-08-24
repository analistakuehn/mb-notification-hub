using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.Audit.Domain;

namespace NotificationHub.Api.Modules.Audit.Infrastructure.Persistence.Configurations;

internal sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> builder)
    {
        // A row is either fully chained or fully pre-chain; a partial chain
        // column set would be evidence of tampering or a defective writer.
        builder.ToTable("audit_event", table => table.HasCheckConstraint(
            "ck_audit_event_chain_complete",
            "(canonical IS NULL AND prev_hash IS NULL AND hash IS NULL) "
            + "OR (canonical IS NOT NULL AND prev_hash IS NOT NULL AND hash IS NOT NULL)"));

        builder.Property(auditEvent => auditEvent.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        // The table is partitioned by month on occurred_at; PostgreSQL requires
        // the partition column inside the primary key, so the key is composite.
        // The id stays globally unique in practice (UUID v7 generated in code).
        builder.HasKey(auditEvent => new { auditEvent.Id, auditEvent.OccurredAt });

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

        // Plain text on purpose: jsonb would rewrite the stored bytes and
        // detach them from the hash they produced.
        builder.Property(auditEvent => auditEvent.Canonical)
            .HasColumnName("canonical")
            .HasColumnType("text");

        builder.Property(auditEvent => auditEvent.PrevHash)
            .HasColumnName("prev_hash");

        builder.Property(auditEvent => auditEvent.Hash)
            .HasColumnName("hash");

        builder.HasIndex(nameof(AuditEvent.EntityType), nameof(AuditEvent.EntityId))
            .HasDatabaseName("ix_audit_event_entity");

        // The tail of the chain, read inside the advisory lock on every append.
        // Descending on the sequence alone: partition pruning already satisfied
        // the time predicate, so leading with the partition column would be a
        // useless prefix, and the remaining predicate is a range rather than an
        // equality, which gives a composite no ordering by sequence inside it.
        // The filter has to appear literally in the reading statement for the
        // planner to match a partial index, and it does; dropping it from that
        // statement costs the index without any error.
        //
        // Declared on the partitioned parent so PostgreSQL propagates it to
        // every partition, including the ones the provisioner creates months
        // from now: a future partition without this index brings back a hold
        // window that grows with the size of the month.
        builder.HasIndex([nameof(AuditEvent.Seq)], "ChainTail")
            .HasDatabaseName("ix_audit_event_chain_tail")
            .IsDescending()
            .HasFilter("hash IS NOT NULL");

        // The other half of the same read, and it costs nothing to keep: rows
        // written before the chain existed are a closed set, so an index over
        // them never takes an insert. What it buys is a partition proving in
        // one lookup that it holds no pre-chain rows, instead of scanning to
        // prove an absence.
        builder.HasIndex([nameof(AuditEvent.Seq)], "PreChain")
            .HasDatabaseName("ix_audit_event_prechain_seq")
            .HasFilter("hash IS NULL");
    }
}
