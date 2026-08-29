using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Persistence.Configurations;

internal sealed class LifecycleNoteConfiguration : IEntityTypeConfiguration<LifecycleNote>
{
    public void Configure(EntityTypeBuilder<LifecycleNote> builder)
    {
        builder.ToTable("lifecycle_note");

        builder.HasKey(note => note.Id);

        // Minted by the aggregate, because the reference has to be known while
        // the audit entry is being built and before anything reaches the
        // database: the trail carries it, and a value the store hands back
        // after the insert would arrive one statement too late.
        builder.Property(note => note.Id)
            .HasColumnName("id")
            .ValueGeneratedNever();

        builder.Property(note => note.SubjectType)
            .HasColumnName("subject_type")
            .HasMaxLength(20);

        builder.Property(note => note.SubjectKey)
            .HasColumnName("subject_key")
            .HasMaxLength(TemplateKey.MaxLength);

        builder.Property(note => note.Application)
            .HasColumnName("application")
            .HasMaxLength(ApplicationName.MaxLength);

        builder.Property(note => note.Text)
            .HasColumnName("text")
            .HasMaxLength(LifecycleNoteText.MaxLength);

        builder.Property(note => note.RecordedBy)
            .HasColumnName("recorded_by")
            .HasMaxLength(200);

        builder.Property(note => note.RecordedAt)
            .HasColumnName("recorded_at");

        // No foreign key, and that is the design: the subject is a template on
        // one row and a layout on the next, so no single column could point at
        // both. It also keeps the note independent of the artifact's own
        // lifetime, which is what lets an erasure remove the prose without
        // touching the governed identity it was written about.
        builder.HasIndex(nameof(LifecycleNote.SubjectType), nameof(LifecycleNote.SubjectKey))
            .HasDatabaseName("ix_lifecycle_note_subject");
    }
}
