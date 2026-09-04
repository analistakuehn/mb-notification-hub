using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Persistence.Configurations;

internal sealed class AttachmentConfiguration : IEntityTypeConfiguration<Attachment>
{
    public void Configure(EntityTypeBuilder<Attachment> builder)
    {
        builder.ToTable("attachment");

        builder.Property(attachment => attachment.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();
        builder.HasKey(attachment => attachment.Id);

        builder.Property(attachment => attachment.Reference)
            .HasColumnName("reference")
            .HasConversion(
                reference => reference.Value,
                value => AttachmentReference.Trusted(value))
            .HasMaxLength(AttachmentReference.Length);

        builder.Property(attachment => attachment.Application)
            .HasColumnName("application")
            .HasMaxLength(Attachment.MaxApplicationLength);

        builder.Property(attachment => attachment.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(Attachment.MaxFileNameLength);

        builder.Property(attachment => attachment.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(Attachment.MaxContentTypeLength);

        builder.Property(attachment => attachment.SizeBytes)
            .HasColumnName("size_bytes");

        builder.Property(attachment => attachment.ContentId)
            .HasColumnName("content_id");

        builder.Property(attachment => attachment.State)
            .HasColumnName("state")
            .HasMaxLength(30);

        builder.Property(attachment => attachment.CreatedAt)
            .HasColumnName("created_at");

        builder.Property(attachment => attachment.ReceivedAt)
            .HasColumnName("received_at");

        // The two columns the verdict leaves behind. They sit on the aggregate
        // and not on the release row because they describe an attachment that
        // was refused or is waiting, and neither of those has a release.
        builder.Property(attachment => attachment.ValidationDetail)
            .HasColumnName("validation_detail")
            .HasMaxLength(Attachment.MaxValidationDetailLength);

        builder.Property(attachment => attachment.InconclusiveUntil)
            .HasColumnName("inconclusive_until");

        // The instant the attachment ended. It sits here and not on the row of
        // the withdrawal because a refusal has no row of its own, and because
        // what the sweep of abandoned content reads is one column on one table
        // rather than a join it would have to do for every candidate.
        builder.Property(attachment => attachment.EndedAt)
            .HasColumnName("ended_at");

        // Nullable and a word, not a flag. The round has to know which repair
        // it is running before it acts, and a flag would send it back to the
        // store, the record and the clock to work that out for every row it
        // read.
        builder.Property(attachment => attachment.ReconciliationLiability)
            .HasColumnName("reconciliation_liability")
            .HasMaxLength(AttachmentLiabilities.MaxLength);

        builder.HasIndex(attachment => attachment.Reference)
            .HasDatabaseName("ux_attachment_reference")
            .IsUnique();

        builder.HasIndex(attachment => attachment.ContentId)
            .HasDatabaseName("ux_attachment_content_id")
            .IsUnique();

        // The index of the outstanding repairs. The filter is what makes it
        // worth having: almost every attachment owes nothing, so the index
        // holds the exception and the round reads a structure the size of the
        // backlog instead of the size of the table.
        //
        // The key is the creation instant and not the word, because the word
        // is not what the round seeks by. It reads whatever is outstanding,
        // oldest first, and a key on the word would give a scan whose rows
        // still have to be sorted; keyed this way the index answers the
        // selection and the order together.
        builder.HasIndex(attachment => attachment.CreatedAt)
            .HasDatabaseName("ix_attachment_reconciliation_liability")
            .HasFilter("reconciliation_liability IS NOT NULL");

        // The index of the states content can still be taken from. The filter
        // is what keeps it small over time: an attachment leaves it for good
        // the moment its content is discarded or released, so what stays is
        // the working set of the sweep and never the history of the table.
        //
        // The list is built from the vocabulary rather than spelled out, so a
        // state renamed in one place cannot leave the filter pointing at a
        // word the column no longer holds.
        //
        // The key is the creation instant, which is the order the sweep drains
        // the backlog in. It is not what any of the four conditions compares:
        // each state counts from an instant of its own, and one index cannot
        // answer four different comparisons, so the comparison is rechecked on
        // the rows the filter leaves.
        //
        // It is named because it shares that key with the index above, and two
        // indexes over the same property are one index to the model builder
        // unless the second is named. Without the name this declaration
        // rewrote the filter and the database name of the other one, and the
        // only thing that reported it was a test over the mapping.
        builder.HasIndex(attachment => attachment.CreatedAt, "ix_attachment_abandonment")
            .HasDatabaseName("ix_attachment_abandonment")
            .HasFilter(DiscardableStatesFilter());

        builder.ToTable(table => table.HasCheckConstraint(
            "ck_attachment_size_positive",
            "size_bytes > 0"));

        builder.Property<uint>("xmin")
            .IsRowVersion();
    }

    /// <summary>
    /// The states the index above holds, written from the vocabulary itself.
    /// A list transcribed here would go on filtering by a word the column had
    /// stopped holding, and nothing about that failing is visible: the index
    /// would simply stop being read.
    /// </summary>
    private static string DiscardableStatesFilter()
        => "state IN ("
            + string.Join(", ", AttachmentStates.Discardable.Select(state => $"'{state}'"))
            + ")";
}
