using System.Globalization;
using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// When the content of an attachment stops being kept, read at the aggregate.
/// Two things are measured here and they are different: which states can be
/// abandoned at all, and what each one counts its window from.
/// <para>
/// The second is the one that costs something to get wrong. Counted from the
/// creation, every window would end an attachment that is being used exactly
/// as intended, so each state counts from the last event that could still have
/// changed it, and every case below separates the two instants far enough that
/// a rule reading the wrong one fails.
/// </para>
/// </summary>
public sealed class AttachmentDiscardStateTests
{
    private static readonly DateTimeOffset Registered = DateTimeOffset.Parse(
        "2026-01-05T09:00:00Z",
        CultureInfo.InvariantCulture);

    /// <summary>
    /// A year between the registration and everything that follows. Any rule
    /// that read the creation instant would answer that the attachment was
    /// abandoned long before its own window opened.
    /// </summary>
    private static readonly DateTimeOffset Acted = Registered.AddYears(1);

    private static readonly TimeSpan InconclusiveWindow = TimeSpan.FromHours(24);

    private static readonly AttachmentRetentionWindows Windows = new(
        UnstartedUpload: TimeSpan.FromDays(7),
        UnvalidatedContent: TimeSpan.FromDays(5),
        RefusedContent: TimeSpan.FromDays(3),
        WithdrawnRelease: TimeSpan.FromDays(11));

    /// <summary>
    /// A ceiling this file picks for itself. Size is not what these oracles
    /// read, and the aggregate is handed a ceiling rather than holding one.
    /// </summary>
    private const long SizeCeiling = 1_048_576;

    /// <summary>
    /// Each state, the instant it counts from, and the window it is kept for.
    /// The four windows are deliberately different, so a rule that read one of
    /// them for another state fails instead of agreeing by coincidence.
    /// </summary>
    public static TheoryData<string, int> AbandonableWindows()
        => new()
        {
            { AttachmentStates.AwaitingUpload, 7 },
            { AttachmentStates.Received, 5 },
            { AttachmentStates.Rejected, 3 },
            { AttachmentStates.Revoked, 11 },
        };

    public static TheoryData<string> AbandonableStates()
        => new()
        {
            AttachmentStates.AwaitingUpload,
            AttachmentStates.Received,
            AttachmentStates.Rejected,
            AttachmentStates.Revoked,
        };

    [Theory]
    [MemberData(nameof(AbandonableWindows))]
    public void Content_is_kept_for_the_window_of_its_own_state(string state, int days)
    {
        Attachment attachment = AttachmentIn(state);
        DateTimeOffset clock = state == AttachmentStates.AwaitingUpload ? Registered : Acted;

        attachment.DiscardableFrom(Windows).ShouldBe(clock.AddDays(days));
    }

    /// <summary>
    /// The instant itself, from both sides. A rule off by any amount fails on
    /// one of the two, and the pair is what keeps the assertion from being
    /// satisfied by a window that never opens or by one that is always open.
    /// </summary>
    [Theory]
    [MemberData(nameof(AbandonableStates))]
    public void Nothing_is_discarded_before_its_deadline_and_the_deadline_itself_discards(
        string state)
    {
        Attachment waiting = AttachmentIn(state);
        DateTimeOffset deadline = waiting.DiscardableFrom(Windows).ShouldNotBeNull();

        waiting.Discard(deadline - TimeSpan.FromTicks(1), Windows)
            .ShouldBe(AttachmentDiscardTransition.NotAbandoned);
        waiting.State.ShouldBe(state);

        Attachment due = AttachmentIn(state);
        due.Discard(deadline, Windows).ShouldBe(AttachmentDiscardTransition.Applied);
        due.State.ShouldBe(AttachmentStates.Discarded);
    }

    /// <summary>
    /// The whole of what separates an attachment that is being used from one
    /// that was left behind. The registration is a year older than the act
    /// that put the attachment in its state, so a window counted from the
    /// creation would have run out long ago for all three.
    /// </summary>
    [Theory]
    [InlineData(AttachmentStates.Received)]
    [InlineData(AttachmentStates.Rejected)]
    [InlineData(AttachmentStates.Revoked)]
    public void A_window_counts_from_the_last_act_and_never_from_the_registration(string state)
    {
        Attachment attachment = AttachmentIn(state);

        attachment.DiscardableFrom(Windows).ShouldNotBeNull().ShouldBeGreaterThan(Acted);
        attachment.Discard(Acted, Windows).ShouldBe(AttachmentDiscardTransition.NotAbandoned);
        attachment.State.ShouldBe(state);
    }

    /// <summary>
    /// The three states nothing here may take content from, whatever the
    /// clock says. A release is what the whole flow exists to produce; a wait
    /// for a verdict has an owner and a deadline of its own and ends in a
    /// refusal, where this clock starts; and an attachment whose content is
    /// already gone has nothing left to take.
    /// </summary>
    [Theory]
    [InlineData(AttachmentStates.Released)]
    [InlineData(AttachmentStates.Inconclusive)]
    [InlineData(AttachmentStates.Discarded)]
    public void A_state_outside_the_rule_is_never_abandoned(string state)
    {
        Attachment attachment = AttachmentIn(state);

        attachment.DiscardableFrom(Windows).ShouldBeNull();
        attachment.Discard(Acted.AddYears(10), Windows)
            .ShouldBe(AttachmentDiscardTransition.NotAbandoned);
        attachment.State.ShouldBe(state);
    }

    /// <summary>
    /// The published list and the rule, held against each other. The list is
    /// what the schema filters on and what a reader of the module sees, and it
    /// is derived here from the aggregate rather than transcribed, so a state
    /// that starts or stops being abandonable cannot leave the two disagreeing.
    /// </summary>
    [Fact]
    public void The_published_list_names_exactly_the_states_the_rule_can_abandon()
    {
        var fromTheRule = AllStates()
            .Where(state => AttachmentIn(state).DiscardableFrom(Windows) is not null)
            .Order(StringComparer.Ordinal)
            .ToArray();

        fromTheRule.ShouldNotBeEmpty();
        AttachmentStates.Discardable.Order(StringComparer.Ordinal).ShouldBe(fromTheRule);
    }

    /// <summary>
    /// A window nobody set is not a window of zero. Zero would remove the
    /// content of every attachment the moment it reached the state, which is a
    /// decision about the product taken by an omission, so the aggregate fails
    /// closed and the startup guard refuses the value long before this.
    /// </summary>
    [Theory]
    [MemberData(nameof(AbandonableStates))]
    public void A_window_nobody_set_never_runs_out(string state)
    {
        Attachment attachment = AttachmentIn(state);

        attachment.DiscardableFrom(default).ShouldBeNull();
        attachment.Discard(Acted.AddYears(10), default)
            .ShouldBe(AttachmentDiscardTransition.NotAbandoned);
        attachment.State.ShouldBe(state);
    }

    /// <summary>
    /// What the discard writes, and everything it leaves alone. The record is
    /// what outlives the content, so the instant of the ending, the detail of
    /// the verdict and the trace of the wait all stay exactly as they were:
    /// they are the only remaining answer to what this attachment was.
    /// </summary>
    [Fact]
    public void Discarding_says_the_content_is_gone_and_rewrites_nothing_else()
    {
        Attachment attachment = AttachmentIn(AttachmentStates.Rejected);
        DateTimeOffset deadline = attachment.DiscardableFrom(Windows).ShouldNotBeNull();

        attachment.Discard(deadline, Windows).ShouldBe(AttachmentDiscardTransition.Applied);

        attachment.State.ShouldBe(AttachmentStates.Discarded);
        attachment.EndedAt.ShouldBe(Acted);
        attachment.ValidationDetail.ShouldBe("content-type-not-admitted");
        attachment.InconclusiveUntil.ShouldBe(Acted + InconclusiveWindow);
        attachment.ReceivedAt.ShouldBe(Acted);
        attachment.CreatedAt.ShouldBe(Registered);
    }

    /// <summary>
    /// The repeat a second round sends after a first one already removed the
    /// content. It changes nothing, and it is the property that keeps an
    /// attachment from being offered to the store round after round for ever.
    /// </summary>
    [Fact]
    public void Discarding_twice_changes_nothing_the_second_time()
    {
        Attachment attachment = AttachmentIn(AttachmentStates.Revoked);
        DateTimeOffset deadline = attachment.DiscardableFrom(Windows).ShouldNotBeNull();

        attachment.Discard(deadline, Windows).ShouldBe(AttachmentDiscardTransition.Applied);
        attachment.Discard(deadline.AddYears(1), Windows)
            .ShouldBe(AttachmentDiscardTransition.NotAbandoned);

        attachment.State.ShouldBe(AttachmentStates.Discarded);
        attachment.DiscardableFrom(Windows).ShouldBeNull();
    }

    /// <summary>
    /// The instant the two acts that end an attachment write, and the one act
    /// that does not. A release ends the verdict and leaves the content
    /// usable, so dating it here would say the attachment ended when it had
    /// just become usable.
    /// </summary>
    [Fact]
    public void Only_the_acts_that_end_the_attachment_write_the_instant_of_the_ending()
    {
        Attachment released = AttachmentIn(AttachmentStates.Released);
        released.EndedAt.ShouldBeNull();

        Attachment waiting = AttachmentIn(AttachmentStates.Inconclusive);
        waiting.EndedAt.ShouldBeNull();

        Attachment registered = AttachmentIn(AttachmentStates.AwaitingUpload);
        registered.EndedAt.ShouldBeNull();

        Attachment received = AttachmentIn(AttachmentStates.Received);
        received.EndedAt.ShouldBeNull();

        AttachmentIn(AttachmentStates.Rejected).EndedAt.ShouldBe(Acted);
        AttachmentIn(AttachmentStates.Revoked).EndedAt.ShouldBe(Acted);
    }

    private static string[] AllStates()
        =>
        [
            AttachmentStates.AwaitingUpload,
            AttachmentStates.Received,
            AttachmentStates.Inconclusive,
            AttachmentStates.Released,
            AttachmentStates.Rejected,
            AttachmentStates.Revoked,
            AttachmentStates.Discarded,
        ];

    /// <summary>
    /// An attachment in the state named, built through the transitions that
    /// really reach it. Every one of them is registered a year before it is
    /// acted on, which is what makes a rule that reads the creation instant
    /// fail here.
    /// </summary>
    private static Attachment AttachmentIn(string state)
    {
        Attachment attachment = state switch
        {
            AttachmentStates.AwaitingUpload => RegisteredAttachment(),
            AttachmentStates.Received => ReceivedAttachment(),
            AttachmentStates.Inconclusive => WaitingAttachment(),
            AttachmentStates.Released => ReleasedAttachment(),
            AttachmentStates.Revoked => RevokedAttachment(),
            AttachmentStates.Discarded => DiscardedAttachment(),
            _ => RefusedAttachment(),
        };

        attachment.State.ShouldBe(state);
        return attachment;
    }

    private static Attachment DiscardedAttachment()
    {
        Attachment attachment = RefusedAttachment();
        attachment.Discard(Acted.AddDays(3), Windows).ShouldBe(AttachmentDiscardTransition.Applied);
        return attachment;
    }

    private static Attachment RevokedAttachment()
    {
        Attachment attachment = ReleasedAttachment();
        attachment.Revoke(Acted).ShouldBe(AttachmentRevocationTransition.Applied);
        return attachment;
    }

    private static Attachment RefusedAttachment()
    {
        Attachment attachment = WaitingAttachment();
        attachment.Reject("content-type-not-admitted", Acted)
            .ShouldBe(AttachmentValidationTransition.Applied);
        return attachment;
    }

    private static Attachment ReleasedAttachment()
    {
        Attachment attachment = WaitingAttachment();
        attachment.Release().ShouldBe(AttachmentValidationTransition.Applied);
        return attachment;
    }

    private static Attachment WaitingAttachment()
    {
        Attachment attachment = ReceivedAttachment();
        attachment.HoldInconclusive("verifier-did-not-answer", Acted, InconclusiveWindow)
            .ShouldBe(AttachmentValidationTransition.Applied);
        return attachment;
    }

    private static Attachment ReceivedAttachment()
    {
        Attachment attachment = RegisteredAttachment();
        attachment.MarkReceived(4, Acted).ShouldBe(AttachmentReceiveOutcome.Received);
        return attachment;
    }

    private static Attachment RegisteredAttachment()
        => Attachment
            .Register("billing-app", "invoice.pdf", "application/pdf", 4, SizeCeiling, Registered)
            .Value
            .ShouldNotBeNull();
}
