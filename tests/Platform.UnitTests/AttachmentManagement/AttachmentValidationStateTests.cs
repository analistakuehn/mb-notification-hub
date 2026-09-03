using System.Globalization;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The transitions of the state machine, read at the aggregate. What they
/// leave durable is measured against a database elsewhere; what they refuse is
/// measured here, because refusing is a decision of the type and not of the
/// storage.
/// </summary>
public sealed class AttachmentValidationStateTests
{
    private static readonly TimeSpan Window = TimeSpan.FromHours(24);

    private static readonly DateTimeOffset Now = DateTimeOffset.Parse(
        "2026-09-02T12:00:00Z",
        CultureInfo.InvariantCulture);

    [Fact]
    public void An_approval_is_the_only_thing_that_reaches_the_released_state()
    {
        Attachment released = ReceivedAttachment();
        Attachment refused = ReceivedAttachment();
        Attachment waiting = ReceivedAttachment();

        released.Release().ShouldBe(AttachmentValidationTransition.Applied);
        refused.Reject("content-type-not-admitted")
            .ShouldBe(AttachmentValidationTransition.Applied);
        waiting.HoldInconclusive("verifier-did-not-answer", Now, Window)
            .ShouldBe(AttachmentValidationTransition.Applied);

        released.State.ShouldBe(AttachmentStates.Released);
        refused.State.ShouldBe(AttachmentStates.Rejected);
        waiting.State.ShouldBe(AttachmentStates.Inconclusive);
    }

    /// <summary>
    /// The waiting state is a state a verdict may still act on, and both ways
    /// out of it are reachable. Without this, the only proven exit from the
    /// wait would be its deadline, and a verifier that answered late would have
    /// nowhere to put the answer.
    /// </summary>
    [Fact]
    public void An_attachment_that_was_waiting_can_still_be_released_or_refused()
    {
        Attachment released = WaitingAttachment();
        Attachment refused = WaitingAttachment();

        released.Release().ShouldBe(AttachmentValidationTransition.Applied);
        refused.Reject("content-type-not-admitted")
            .ShouldBe(AttachmentValidationTransition.Applied);

        released.State.ShouldBe(AttachmentStates.Released);
        refused.State.ShouldBe(AttachmentStates.Rejected);

        // The detail of the wait stops describing a released attachment and
        // becomes the detail of the refusal on a refused one.
        released.ValidationDetail.ShouldBeNull();
        refused.ValidationDetail.ShouldBe("content-type-not-admitted");
    }

    [Fact]
    public void An_attachment_whose_content_never_arrived_takes_no_verdict_at_all()
    {
        Attachment attachment = RegisteredAttachment();

        attachment.VerdictRefusal().ShouldBe(AttachmentValidationTransition.NotReceived);
        attachment.Release().ShouldBe(AttachmentValidationTransition.NotReceived);
        attachment.Reject("content-not-inspectable")
            .ShouldBe(AttachmentValidationTransition.NotReceived);
        attachment.HoldInconclusive("verifier-did-not-answer", Now, Window)
            .ShouldBe(AttachmentValidationTransition.NotReceived);

        attachment.State.ShouldBe(AttachmentStates.AwaitingUpload);
        attachment.ValidationDetail.ShouldBeNull();
        attachment.InconclusiveUntil.ShouldBeNull();
    }

    [Theory]
    [InlineData(AttachmentStates.Released)]
    [InlineData(AttachmentStates.Rejected)]
    [InlineData(AttachmentStates.Revoked)]
    public void A_settled_attachment_is_not_reopened_and_is_not_released_again(string settled)
    {
        Attachment attachment = SettledAttachment(settled);

        attachment.VerdictRefusal().ShouldBe(AttachmentValidationTransition.AlreadyDecided);
        attachment.Release().ShouldBe(AttachmentValidationTransition.AlreadyDecided);
        attachment.Reject("content-not-inspectable")
            .ShouldBe(AttachmentValidationTransition.AlreadyDecided);
        attachment.HoldInconclusive("verifier-did-not-answer", Now, Window)
            .ShouldBe(AttachmentValidationTransition.AlreadyDecided);

        attachment.State.ShouldBe(settled);
    }

    [Fact]
    public void A_repeated_open_verdict_does_not_move_the_deadline_it_started()
    {
        Attachment attachment = ReceivedAttachment();

        attachment.HoldInconclusive("verifier-did-not-answer", Now, Window);
        DateTimeOffset first = attachment.InconclusiveUntil.ShouldNotBeNull();
        attachment.HoldInconclusive(
            "verifier-answered-late",
            Now + TimeSpan.FromHours(12),
            Window);

        first.ShouldBe(Now + Window);
        attachment.InconclusiveUntil.ShouldBe(first);

        // The detail is the latest one, and only the deadline is the thing
        // nothing moves: a second reading is news, a second clock is not.
        attachment.ValidationDetail.ShouldBe("verifier-answered-late");
    }

    /// <summary>
    /// A wider window on a later verdict is the shape that would extend the
    /// wait without looking like it: the deadline belongs to the first verdict
    /// and to no configuration read afterwards.
    /// </summary>
    [Fact]
    public void A_wider_window_on_a_later_verdict_does_not_extend_the_wait()
    {
        Attachment attachment = ReceivedAttachment();

        attachment.HoldInconclusive("verifier-did-not-answer", Now, Window);
        attachment.HoldInconclusive(
            "verifier-did-not-answer",
            Now,
            Window + TimeSpan.FromDays(7));

        attachment.InconclusiveUntil.ShouldBe(Now + Window);
    }

    [Fact]
    public void The_wait_ends_at_the_deadline_and_not_before_it()
    {
        Attachment attachment = WaitingAttachment();

        attachment.InconclusiveWindowHasElapsed(Now).ShouldBeFalse();
        attachment.InconclusiveWindowHasElapsed(Now + Window - TimeSpan.FromTicks(1))
            .ShouldBeFalse();
        attachment.InconclusiveWindowHasElapsed(Now + Window).ShouldBeTrue();
    }

    [Fact]
    public void A_state_that_is_not_waiting_on_a_verdict_has_no_deadline_to_reach()
    {
        Attachment received = ReceivedAttachment();
        Attachment released = WaitingAttachment();
        released.Release();

        received.InconclusiveWindowHasElapsed(Now + TimeSpan.FromDays(400)).ShouldBeFalse();

        // The deadline of the wait it went through stays on the row, and it
        // stops describing the attachment the moment the wait ended.
        released.InconclusiveUntil.ShouldBe(Now + Window);
        released.InconclusiveWindowHasElapsed(Now + TimeSpan.FromDays(400)).ShouldBeFalse();
    }

    [Fact]
    public void A_verdict_with_no_detail_at_all_is_refused_by_the_transition_itself()
    {
        Attachment refused = ReceivedAttachment();
        Attachment waiting = ReceivedAttachment();

        Should.Throw<ArgumentException>(() => refused.Reject("  "));
        Should.Throw<ArgumentException>(() => waiting.HoldInconclusive("  ", Now, Window));

        refused.State.ShouldBe(AttachmentStates.Received);
        waiting.State.ShouldBe(AttachmentStates.Received);
        waiting.InconclusiveUntil.ShouldBeNull();
    }

    [Fact]
    public void A_detail_the_durable_state_cannot_hold_is_not_a_usable_detail()
    {
        Attachment.IsUsableDetail("content-not-inspectable").ShouldBeTrue();
        Attachment.IsUsableDetail(new string('d', Attachment.MaxValidationDetailLength))
            .ShouldBeTrue();
        Attachment.IsUsableDetail(new string('d', Attachment.MaxValidationDetailLength + 1))
            .ShouldBeFalse();
        Attachment.IsUsableDetail("  ").ShouldBeFalse();
        Attachment.IsUsableDetail(null).ShouldBeFalse();
    }

    private static Attachment SettledAttachment(string state)
    {
        Attachment attachment = ReceivedAttachment();
        if (state == AttachmentStates.Rejected)
        {
            attachment.Reject("content-type-not-admitted");
        }
        else
        {
            attachment.Release();
        }

        // A release that was taken back settles the attachment through a path
        // of its own, and the rule above has to hold over it too: without this
        // arm, a verdict could grant a second release over an attachment whose
        // first one an authority had already withdrawn.
        if (state == AttachmentStates.Revoked)
        {
            attachment.Revoke();
        }

        attachment.State.ShouldBe(state);
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
