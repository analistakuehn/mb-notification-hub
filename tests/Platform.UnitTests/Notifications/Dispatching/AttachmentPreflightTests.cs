using Microsoft.Extensions.Logging.Abstractions;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NSubstitute;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

/// <summary>
/// The revalidation that runs between the claim of an attempt and the call
/// that cannot be taken back, read as the mapping it is: from what the stored
/// document says and what the owning module answers, to the one thing the
/// dispatch needs to decide, which is whether this send may happen and, if
/// not, whether the attempt is finished or merely held.
/// <para>
/// The two refusals are not interchangeable. A set that may not be used will
/// not become usable by being asked again, so the attempt ends; a set nothing
/// could be established about is a question that may answer next time, so the
/// attempt goes back. Collapsing them would either end notifications because a
/// store blinked or hold, forever, sends that are never going to happen.
/// </para>
/// </summary>
public sealed class AttachmentPreflightTests
{
    /// <summary>
    /// A notification that named no attachments is the ordinary case and the
    /// one where nothing is asked of the owning module at all.
    /// <para>
    /// The absence is asserted next to a presence: the same doubles are asked
    /// exactly once when a set is there, so a preflight that had stopped
    /// calling them altogether could not pass both halves.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_notification_with_no_accepted_set_is_clear_without_asking_anything()
    {
        IAttachmentEnvelopeCheck envelope = Envelope(AttachmentEnvelopeVerdict.WithinEnvelope);
        IAttachmentReleaseCheck release = Release(AttachmentReleaseVerdict.Deliverable);

        (await Preflight(envelope, release).VerifyAsync(Accepted(), CancellationToken.None))
            .ShouldBe(AttachmentPreflightOutcome.Clear);

        envelope.DidNotReceiveWithAnyArgs().Measure(default!);
        await release.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);

        (await Preflight(envelope, release).VerifyAsync(WithSet(), CancellationToken.None))
            .ShouldBe(AttachmentPreflightOutcome.Clear);

        envelope.ReceivedWithAnyArgs(1).Measure(default!);
        await release.ReceivedWithAnyArgs(1).VerifyAsync(default!, default);
    }

    /// <summary>
    /// A document that stopped reading is met by the gate that runs before the
    /// claim, so meeting one here means the row changed underneath this send.
    /// Nothing is settled about an attempt nothing can be said about, and
    /// neither check is asked over a set nobody can name.
    /// </summary>
    [Fact]
    public async Task A_stored_document_that_no_longer_reads_is_undecided_and_asks_nothing()
    {
        IAttachmentEnvelopeCheck envelope = Envelope(AttachmentEnvelopeVerdict.WithinEnvelope);
        IAttachmentReleaseCheck release = Release(AttachmentReleaseVerdict.Deliverable);
        Notification notification = Accepted();
        notification.FreezeAcceptedAttachments("""{"schemaVersion":9,"items":[]}""");

        (await Preflight(envelope, release).VerifyAsync(notification, CancellationToken.None))
            .ShouldBe(AttachmentPreflightOutcome.Undecided);

        envelope.DidNotReceiveWithAnyArgs().Measure(default!);
        await release.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    /// <summary>
    /// The capacity is measured first because it reads no store, so a set that
    /// could not go out whatever its releases say never costs a reading of the
    /// durable record.
    /// </summary>
    [Fact]
    public async Task A_set_past_the_capacity_is_refused_before_the_record_is_read()
    {
        IAttachmentEnvelopeCheck envelope = Envelope(AttachmentEnvelopeVerdict.Exceeded);
        IAttachmentReleaseCheck release = Release(AttachmentReleaseVerdict.Deliverable);

        (await Preflight(envelope, release).VerifyAsync(WithSet(), CancellationToken.None))
            .ShouldBe(AttachmentPreflightOutcome.OverCapacity);

        envelope.ReceivedWithAnyArgs(1).Measure(default!);
        await release.DidNotReceiveWithAnyArgs().VerifyAsync(default!, default);
    }

    [Fact]
    public async Task A_set_the_owning_module_calls_deliverable_clears_the_send()
        => (await VerifyAsync(AttachmentReleaseVerdict.Deliverable))
            .ShouldBe(AttachmentPreflightOutcome.Clear);

    [Fact]
    public async Task A_set_the_owning_module_withholds_ends_the_attempt()
        => (await VerifyAsync(AttachmentReleaseVerdict.Withheld))
            .ShouldBe(AttachmentPreflightOutcome.Withheld);

    /// <summary>
    /// A check that did not conclude holds the attempt rather than ending it.
    /// The store being briefly unreachable is not a statement that the set may
    /// not be used, and settling on it would end a notification for a reason
    /// that answers differently a moment later.
    /// </summary>
    [Fact]
    public async Task A_check_that_did_not_conclude_holds_the_attempt_instead_of_ending_it()
        => (await VerifyAsync(AttachmentReleaseVerdict.Unavailable))
            .ShouldBe(AttachmentPreflightOutcome.Undecided);

    /// <summary>
    /// A verdict outside the vocabulary this code knows, which is what a
    /// member added to the published contract would look like here. It reads
    /// as the absence of a statement rather than as one, so the send waits
    /// instead of happening and the attempt is neither ended nor stranded.
    /// </summary>
    [Fact]
    public async Task A_verdict_this_code_does_not_know_holds_the_send_instead_of_clearing_it()
        => (await Preflight(
                Envelope(AttachmentEnvelopeVerdict.WithinEnvelope),
                Release((AttachmentReleaseVerdict)int.MaxValue))
            .VerifyAsync(WithSet(), CancellationToken.None))
            .ShouldBe(AttachmentPreflightOutcome.Undecided);

    /// <summary>
    /// Stand-ins nobody configured, which is what a composition that forgot to
    /// register the real checks would hand this. Both answer their zero, both
    /// zeros refuse, and the send does not happen.
    /// </summary>
    [Fact]
    public async Task Checks_that_were_never_told_what_to_answer_refuse_the_send()
        => (await Preflight(
                Substitute.For<IAttachmentEnvelopeCheck>(),
                Substitute.For<IAttachmentReleaseCheck>())
            .VerifyAsync(WithSet(), CancellationToken.None))
            .ShouldBe(AttachmentPreflightOutcome.OverCapacity);

    [Fact]
    public async Task A_preflight_over_nothing_is_refused_outright()
        => await Should.ThrowAsync<ArgumentNullException>(() => Preflight(
                Envelope(AttachmentEnvelopeVerdict.WithinEnvelope),
                Release(AttachmentReleaseVerdict.Deliverable))
            .VerifyAsync(null!, CancellationToken.None));

    private static async Task<AttachmentPreflightOutcome> VerifyAsync(
        AttachmentReleaseVerdict verdict)
        => await Preflight(Envelope(AttachmentEnvelopeVerdict.WithinEnvelope), Release(verdict))
            .VerifyAsync(WithSet(), CancellationToken.None);

    private static AttachmentPreflight Preflight(
        IAttachmentEnvelopeCheck envelope,
        IAttachmentReleaseCheck release)
        => new(envelope, release, NullLogger<AttachmentPreflight>.Instance);

    private static IAttachmentEnvelopeCheck Envelope(AttachmentEnvelopeVerdict verdict)
    {
        IAttachmentEnvelopeCheck check = Substitute.For<IAttachmentEnvelopeCheck>();
        check.Measure(Arg.Any<AcceptedAttachmentSet>()).Returns(verdict);
        return check;
    }

    private static IAttachmentReleaseCheck Release(AttachmentReleaseVerdict verdict)
    {
        IAttachmentReleaseCheck check = Substitute.For<IAttachmentReleaseCheck>();
        check.VerifyAsync(Arg.Any<AcceptedAttachmentSet>(), Arg.Any<CancellationToken>())
            .Returns(verdict);
        return check;
    }

    private static Notification WithSet()
    {
        Notification notification = Accepted();
        notification.FreezeAcceptedAttachments(AcceptedAttachmentManifest.Serialize(
            AcceptedAttachmentSet.Of([
                new AcceptedAttachment
                {
                    Reference = "att_01J5X9",
                    ContentIdentity = "aci_01J5X9",
                    Name = "contrato.pdf",
                    MediaType = "application/pdf",
                    Length = 2048,
                },
            ])));
        return notification;
    }

    private static Notification Accepted() => Notification.Accept(new NotificationDraft
    {
        Application = "araia-cambio",
        IdempotencyKey = "key-01J5X9",
        RecipientId = "cus_01J5X9",
        Class = NotificationClasses.Transactional,
        TemplateKey = "billing.invoice",
        TemplateVersion = 1,
        VariablesMaskedJson = "{}",
        RequestedBy = "producer",
        TtlSeconds = 3600,
        AcceptedAt = DateTimeOffset.UtcNow,
    });
}
