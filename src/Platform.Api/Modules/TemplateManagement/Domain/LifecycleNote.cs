namespace NotificationHub.Api.Modules.TemplateManagement.Domain;

/// <summary>
/// The words an operator writes beside the canonical reason of a deprecate or
/// a disable, carried as a type and never as a bare string.
/// <para>
/// The type exists for a mechanical reason, not for elegance. The audit trail
/// is append-only by database trigger and hash-chained per partition, so a
/// value that lands in it lands forever, and the text of a note is the one
/// field of this transition nobody can promise anything about: it is free
/// prose typed under pressure while traffic is being stopped. Keeping it in a
/// type means the only way to reach the prose is to name <c>Text</c>, and the
/// producers of audit details are scanned for exactly that name. A
/// <c>string</c> on the command offers no such handle: it reads the same as
/// the reason code beside it.
/// </para>
/// </summary>
public sealed class LifecycleNoteText
{
    /// <summary>
    /// The single ceiling on a note, read by the endpoint validators and by
    /// the column that stores it. Two numbers over one field is how a refusal
    /// that should be a 400 at the door becomes a 500 at the insert.
    /// </summary>
    public const int MaxLength = 500;

    private LifecycleNoteText(string text) => Text = text;

    /// <summary>The operator's own words, exactly as they were typed.</summary>
    public string Text { get; }

    /// <summary>
    /// The note as it arrives from the transport, or nothing. Absent, empty
    /// and blank all mean the same here: an operator who wrote no note wrote
    /// no note, and storing a row of spaces would leave a reference in the
    /// trail pointing at nothing an auditor could read.
    /// </summary>
    public static LifecycleNoteText? Create(string? note)
        => string.IsNullOrWhiteSpace(note) ? null : new LifecycleNoteText(note.Trim());
}

/// <summary>
/// One lifecycle note, stored by this context and outside the audit trail.
/// <para>
/// The trail records the canonical reason and a reference to this row, never
/// the prose. The split is what makes the prose erasable: the trail refuses
/// correction by trigger and every row is covered by the hash chain of its
/// partition, so a sentence written there cannot be taken back, while a row
/// here can be deleted and the deletion itself recorded. An auditor still
/// reads a code for every transition, and a reader who follows the reference
/// after an erasure finds the erasure event that says so.
/// </para>
/// <para>
/// The reference is a random identifier and never a digest of the text. A
/// digest of a short, low-entropy value is not a pseudonym: the whole space of
/// a national identifier falls in seconds on one thread. A digest is also
/// stable, so two transitions carrying the same sentence would stay linked by
/// equal references forever, which is precisely the link an erasure is
/// supposed to break.
/// </para>
/// </summary>
public sealed class LifecycleNote
{
    /// <summary>Random reference the trail carries in place of the prose.</summary>
    public Guid Id { get; private set; }

    /// <summary>The kind of artifact the transition took out of circulation.</summary>
    public string SubjectType { get; private set; } = null!;

    /// <summary>Key of the template or of the layout, as this context names it.</summary>
    public string SubjectKey { get; private set; } = null!;

    /// <summary>Owning application, absent for a layout, which serves every application.</summary>
    public string? Application { get; private set; }

    /// <summary>What the operator wrote. The one field an erasure removes.</summary>
    public string Text { get; private set; } = null!;

    /// <summary>Identity-provider id of whoever wrote it.</summary>
    public string RecordedBy { get; private set; } = null!;

    public DateTimeOffset RecordedAt { get; private set; }

    /// <summary>
    /// The row for a transition that carried a note, or nothing when it
    /// carried none. Returning null for an absent note is what keeps the four
    /// call sites free of a branch each.
    /// </summary>
    public static LifecycleNote? For(
        LifecycleNoteText? note,
        string subjectType,
        string subjectKey,
        string? application,
        string recordedBy,
        DateTimeOffset recordedAt)
    {
        if (note is null)
        {
            return null;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(subjectType);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(recordedBy);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(
            note.Text.Length,
            LifecycleNoteText.MaxLength,
            nameof(note));

        return new LifecycleNote
        {
            Id = Guid.CreateVersion7(),
            SubjectType = subjectType,
            SubjectKey = subjectKey,
            Application = application,
            Text = note.Text,
            RecordedBy = recordedBy,
            RecordedAt = recordedAt,
        };
    }
}
