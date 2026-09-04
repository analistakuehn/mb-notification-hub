using NotificationHub.Api.Modules.Audit.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

/// <summary>
/// The note an operator writes beside the reason code of a deprecate or a
/// disable, and the reference the trail records in its place. The trail is
/// append-only and hash-chained, so what these cases protect is the one
/// property the split exists for: the reference has to be able to point at
/// nothing later, and it must not say anything about the words it replaced.
/// </summary>
public sealed class LifecycleNoteTests
{
    private static readonly DateTimeOffset Moment =
        new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void A_transition_without_words_stores_no_row_and_leaves_no_reference(string? note)
    {
        LifecycleNoteText.Create(note).ShouldBeNull();
        Record(LifecycleNoteText.Create(note)).ShouldBeNull();
    }

    [Fact]
    public void The_words_are_stored_exactly_as_they_were_typed()
    {
        LifecycleNote? note = Record(LifecycleNoteText.Create("  OTP saindo com o produto errado  "));

        note.ShouldNotBeNull();
        note.Text.ShouldBe("OTP saindo com o produto errado");
    }

    /// <summary>
    /// The case that rules out a digest. A reference derived from the words
    /// would be equal whenever the words are, which links every transition
    /// that carried the same sentence, forever, in a store that cannot be
    /// rewritten; and a digest over a short value is a lookup table away from
    /// the value itself. Two identical notes therefore have to be two
    /// references.
    /// </summary>
    [Fact]
    public void The_same_words_written_twice_do_not_share_a_reference()
    {
        const string Same = "conteudo incorreto em producao";

        LifecycleNote? first = Record(LifecycleNoteText.Create(Same));
        LifecycleNote? second = Record(LifecycleNoteText.Create(Same));

        first.ShouldNotBeNull();
        second.ShouldNotBeNull();
        first.Text.ShouldBe(second.Text);
        first.Id.ShouldNotBe(second.Id);
        first.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public void A_note_longer_than_the_ceiling_the_door_enforces_is_refused()
    {
        var atTheCeiling =
            LifecycleNoteText.Create(new string('a', LifecycleNoteText.MaxLength));
        var oneOver =
            LifecycleNoteText.Create(new string('a', LifecycleNoteText.MaxLength + 1));

        Record(atTheCeiling).ShouldNotBeNull();
        Should.Throw<ArgumentOutOfRangeException>(() => Record(oneOver));
    }

    [Fact]
    public void A_note_records_the_subject_it_was_written_about_and_who_wrote_it()
    {
        LifecycleNote? note = Record(LifecycleNoteText.Create("aposentando"));

        note.ShouldNotBeNull();
        note.SubjectType.ShouldBe(AuditEntityTypes.Template);
        note.SubjectKey.ShouldBe("araia.cambio.otp");
        note.Application.ShouldBe("araia-cambio");
        note.RecordedBy.ShouldBe("publisher-1");
        note.RecordedAt.ShouldBe(Moment);
    }

    /// <summary>
    /// A layout serves every application, so its note carries none. The column
    /// is nullable for that reason and not by accident, and the trail entry of
    /// a layout transition leaves the same field absent.
    /// </summary>
    [Fact]
    public void A_note_about_a_layout_carries_no_application()
    {
        var note = LifecycleNote.For(
            LifecycleNoteText.Create("identidade visual antiga"),
            AuditEntityTypes.Layout,
            "araia.wrapper.padrao",
            application: null,
            "publisher-1",
            Moment);

        note.ShouldNotBeNull();
        note.Application.ShouldBeNull();
        note.SubjectType.ShouldBe(AuditEntityTypes.Layout);
    }

    private static LifecycleNote? Record(LifecycleNoteText? note)
        => LifecycleNote.For(
            note,
            AuditEntityTypes.Template,
            "araia.cambio.otp",
            "araia-cambio",
            "publisher-1",
            Moment);
}
