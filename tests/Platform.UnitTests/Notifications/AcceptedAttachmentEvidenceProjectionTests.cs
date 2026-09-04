using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Reads;
using NotificationHub.Api.Modules.Notifications.Integration.V1;

namespace NotificationHub.UnitTests.Notifications;

/// <summary>
/// What the evidence answer says about the accepted set, projected from the
/// stored document and from what the owning module still records.
/// <para>
/// The three outcomes of the document have to stay three all the way to the
/// answer. A notification that named no attachments and one whose document
/// nobody can read are the pair this file exists for: told apart they are two
/// facts, folded together they are one sentence that says a notification
/// carried nothing when nobody can say what it carried.
/// </para>
/// </summary>
public sealed class AcceptedAttachmentEvidenceProjectionTests
{
    private const string Handle = "aci_2f7c1d0a8b3e4f5a9c6d7e8f0a1b2c3d";

    private static readonly DateTimeOffset Anchor =
        new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_notification_that_named_no_attachments_answers_an_empty_set_and_no_refusal()
    {
        AcceptedAttachmentsEvidenceView projected = AcceptedAttachmentEvidenceProjection.Project(
            new AcceptedManifestRead.Absent(), Recorded());

        // Empty and present, never absent: the empty list is the assertion
        // that this notification named nothing.
        projected.Accepted.ShouldNotBeNull().ShouldBeEmpty();
        projected.Refusal.ShouldBeNull();
    }

    [Fact]
    public void A_document_nobody_can_read_answers_no_set_at_all_and_names_the_defect()
    {
        AcceptedAttachmentsEvidenceView projected = AcceptedAttachmentEvidenceProjection.Project(
            new AcceptedManifestRead.Unreadable(
                AcceptedAttachmentManifest.RefusedUnknownSchemaVersion),
            Recorded());

        // The pair that must never read like the one above. A list here, empty
        // or not, would tell an auditor what this row cannot say.
        projected.Accepted.ShouldBeNull();
        projected.Refusal.ShouldBe(AcceptedAttachmentManifest.RefusedUnknownSchemaVersion);
    }

    [Fact]
    public void The_set_carries_the_frozen_composition_and_the_record_of_the_content()
    {
        AcceptedAttachmentsEvidenceView projected = AcceptedAttachmentEvidenceProjection.Project(
            Present(), Recorded(Evidence()));

        AcceptedAttachmentEvidence member = projected.Accepted.ShouldNotBeNull().ShouldHaveSingleItem();
        projected.Refusal.ShouldBeNull();

        // The composition comes from the row, which is the only authority on
        // what was accepted.
        member.Reference.ShouldBe("att_alpha");
        member.ContentIdentity.ShouldBe(Handle);
        member.Name.ShouldBe("comprovante.pdf");
        member.MediaType.ShouldBe("application/pdf");
        member.Length.ShouldBe(2048);

        // The proof of the bytes comes from the owning module, joined on the
        // handle the row froze and on nothing else.
        AttachmentEvidence recorded = member.Recorded.ShouldNotBeNull();
        recorded.Digest.ShouldBe(new string('a', 64));
        recorded.DigestAlgorithm.ShouldBe("sha-256");
        recorded.State.ShouldBe("released");
    }

    /// <summary>
    /// The half that can go missing goes missing on its own. What the
    /// acceptance froze is still answered, because the row still says it, and
    /// the auditor reads that the proof is out of reach rather than that the
    /// notification carried nothing.
    /// </summary>
    [Fact]
    public void A_handle_the_owning_module_no_longer_answers_for_leaves_the_record_absent()
    {
        AcceptedAttachmentsEvidenceView projected = AcceptedAttachmentEvidenceProjection.Project(
            Present(), Recorded());

        AcceptedAttachmentEvidence member = projected.Accepted.ShouldNotBeNull().ShouldHaveSingleItem();
        member.Recorded.ShouldBeNull();
        member.Reference.ShouldBe("att_alpha");
        member.Name.ShouldBe("comprovante.pdf");
    }

    /// <summary>
    /// An answer under another handle is not an answer about this member. The
    /// join is the frozen handle, and a projection that took whatever the
    /// module returned would attach the proof of other bytes to this one.
    /// </summary>
    [Fact]
    public void A_record_returned_under_another_handle_is_not_attached_to_this_member()
    {
        AcceptedAttachmentsEvidenceView projected = AcceptedAttachmentEvidenceProjection.Project(
            Present(), Recorded(Evidence() with { ContentIdentity = "aci_" + new string('b', 32) }));

        projected.Accepted.ShouldNotBeNull().ShouldHaveSingleItem().Recorded.ShouldBeNull();
    }

    [Fact]
    public void The_handles_asked_about_are_the_ones_the_document_froze_and_no_others()
    {
        AcceptedAttachmentEvidenceProjection.HandlesOf(Present()).ShouldBe([Handle]);
        AcceptedAttachmentEvidenceProjection.HandlesOf(new AcceptedManifestRead.Absent())
            .ShouldBeEmpty();
        AcceptedAttachmentEvidenceProjection
            .HandlesOf(new AcceptedManifestRead.Unreadable(
                AcceptedAttachmentManifest.RefusedMalformedDocument))
            .ShouldBeEmpty();
    }

    private static AcceptedManifestRead.Present Present()
        => new(AcceptedAttachmentSet.Of(
        [
            new AcceptedAttachment
            {
                Reference = "att_alpha",
                ContentIdentity = Handle,
                Name = "comprovante.pdf",
                MediaType = "application/pdf",
                Length = 2048,
            },
        ]));

    private static Dictionary<string, AttachmentEvidence> Recorded(
        params AttachmentEvidence[] entries)
        => entries.ToDictionary(entry => entry.ContentIdentity, StringComparer.Ordinal);

    private static AttachmentEvidence Evidence()
        => new()
        {
            ContentIdentity = Handle,
            Reference = "att_alpha",
            Application = "billing",
            State = "released",
            DigestAlgorithm = "sha-256",
            Digest = new string('a', 64),
            DigestedLengthBytes = 2048,
            CapturedAt = Anchor,
            ReleasedAt = Anchor.AddMinutes(1),
        };
}
