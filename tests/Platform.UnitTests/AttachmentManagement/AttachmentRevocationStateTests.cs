using System.Globalization;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The act that takes a release back, read at the aggregate. What separates it
/// from a refusal is measured here rather than described: a refusal is the
/// outcome of reading the content and it writes the detail of the check that
/// refused; a revocation reads nothing, so it moves the state and leaves every
/// column the validation owns exactly as it found it.
/// </summary>
public sealed class AttachmentRevocationStateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-02T12:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public void Taking_a_release_back_ends_the_attachment_without_touching_the_verdict()
    {
        Attachment attachment = ReleasedAttachment();

        attachment.Revoke().ShouldBe(AttachmentRevocationTransition.Applied);

        attachment.State.ShouldBe(AttachmentStates.Revoked);

        // The two columns the validation owns. A revocation that wrote a
        // detail here would say a check refused bytes that no check looked at
        // again, and one that cleared the deadline would erase the trace of a
        // wait the attachment really went through.
        attachment.ValidationDetail.ShouldBeNull();
        attachment.InconclusiveUntil.ShouldBe(Now + Window);
    }

    /// <summary>
    /// The repeat a producer sends after a lost response. It answers that the
    /// state is already the one it asked for, and it is not a failure: a caller
    /// that had to tell a first call from a second would have to remember
    /// whether its own request arrived.
    /// </summary>
    [Fact]
    public void Repeating_a_revocation_changes_nothing_and_says_so()
    {
        Attachment attachment = ReleasedAttachment();

        attachment.Revoke().ShouldBe(AttachmentRevocationTransition.Applied);
        attachment.Revoke().ShouldBe(AttachmentRevocationTransition.AlreadyRevoked);
        attachment.Revoke().ShouldBe(AttachmentRevocationTransition.AlreadyRevoked);

        attachment.State.ShouldBe(AttachmentStates.Revoked);
        attachment.RevocationRefusal().ShouldBe(AttachmentRevocationTransition.AlreadyRevoked);
    }

    [Theory]
    [InlineData(AttachmentStates.AwaitingUpload)]
    [InlineData(AttachmentStates.Received)]
    [InlineData(AttachmentStates.Inconclusive)]
    [InlineData(AttachmentStates.Rejected)]
    public void A_state_that_carries_no_release_has_nothing_to_take_back(string state)
    {
        Attachment attachment = AttachmentIn(state);

        attachment.RevocationRefusal().ShouldBe(AttachmentRevocationTransition.NotReleased);
        attachment.Revoke().ShouldBe(AttachmentRevocationTransition.NotReleased);

        attachment.State.ShouldBe(state);
    }

    /// <summary>
    /// A revoked attachment is settled for the verdict machine too, and it is
    /// settled through a path of its own: the release was granted and taken
    /// back, so a verdict that reopened it would grant a second release without
    /// the explicit revalidation that is the only thing allowed to write one.
    /// </summary>
    [Fact]
    public void A_revoked_attachment_takes_no_verdict_at_all()
    {
        Attachment attachment = ReleasedAttachment();
        attachment.Revoke().ShouldBe(AttachmentRevocationTransition.Applied);

        attachment.VerdictRefusal().ShouldBe(AttachmentValidationTransition.AlreadyDecided);
        attachment.Release().ShouldBe(AttachmentValidationTransition.AlreadyDecided);
        attachment.Reject("content-type-not-admitted")
            .ShouldBe(AttachmentValidationTransition.AlreadyDecided);
        attachment.HoldInconclusive("verifier-did-not-answer", Now, Window)
            .ShouldBe(AttachmentValidationTransition.AlreadyDecided);

        attachment.State.ShouldBe(AttachmentStates.Revoked);
        attachment.ValidationDetail.ShouldBeNull();
    }

    /// <summary>
    /// The waiting state answers the same way the states that never carried a
    /// release do, and it is worth its own reading: it is the one state a
    /// verdict may still act on, so a rule that confused "a verdict may act"
    /// with "a release exists" would take back an approval nobody granted.
    /// </summary>
    [Fact]
    public void A_wait_that_a_verdict_may_still_end_is_not_a_release()
    {
        Attachment waiting = WaitingAttachment();

        waiting.VerdictRefusal().ShouldBeNull();
        waiting.Revoke().ShouldBe(AttachmentRevocationTransition.NotReleased);

        waiting.State.ShouldBe(AttachmentStates.Inconclusive);
        waiting.InconclusiveUntil.ShouldBe(Now + Window);
    }

    private static Attachment AttachmentIn(string state)
    {
        Attachment attachment = state switch
        {
            AttachmentStates.AwaitingUpload => RegisteredAttachment(),
            AttachmentStates.Received => ReceivedAttachment(),
            AttachmentStates.Inconclusive => WaitingAttachment(),
            _ => RefusedAttachment(),
        };

        attachment.State.ShouldBe(state);
        return attachment;
    }

    private static Attachment RefusedAttachment()
    {
        Attachment attachment = ReceivedAttachment();
        attachment.Reject("content-type-not-admitted")
            .ShouldBe(AttachmentValidationTransition.Applied);
        return attachment;
    }

    /// <summary>
    /// An attachment that waited and was then released. The wait is part of the
    /// arrangement on purpose: it is what leaves a deadline on the row, and the
    /// deadline is what a revocation could quietly erase.
    /// </summary>
    private static Attachment ReleasedAttachment()
    {
        Attachment attachment = WaitingAttachment();
        attachment.Release().ShouldBe(AttachmentValidationTransition.Applied);
        return attachment;
    }

    private static Attachment WaitingAttachment()
    {
        Attachment attachment = ReceivedAttachment();
        attachment.HoldInconclusive("verifier-did-not-answer", Now, Window)
            .ShouldBe(AttachmentValidationTransition.Applied);
        return attachment;
    }

    private static Attachment ReceivedAttachment()
    {
        Attachment attachment = RegisteredAttachment();
        attachment.MarkReceived(4, Now).ShouldBe(AttachmentReceiveOutcome.Received);
        return attachment;
    }

    private static Attachment RegisteredAttachment()
        => Attachment.Register("billing-app", "invoice.pdf", "application/pdf", 4, Now)
            .Value
            .ShouldNotBeNull();
}
