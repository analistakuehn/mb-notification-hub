using Microsoft.Extensions.Options;
using NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Capacity;
using NotificationHub.Api.Modules.AttachmentManagement.Integration.V1;

namespace NotificationHub.UnitTests.AttachmentManagement;

/// <summary>
/// The measurement of an accepted set against the capacity in force, which is
/// the half of the revalidation that reads no store at all.
/// <para>
/// It answers over the lengths the snapshot froze, so it says whether the set
/// as accepted fits and never whether the bytes behind it still exist. The
/// numbers below are the test's own and none of them is the approved capacity:
/// what is under test is the arithmetic and the direction of every boundary,
/// and the approved values are read from the shipped configuration by the
/// guard that owns them.
/// </para>
/// </summary>
public sealed class AcceptedSetEnvelopeCheckTests
{
    private const long Envelope = 1_000;

    private const int Members = 3;

    [Fact]
    public void A_set_inside_both_limits_is_within_the_envelope()
        => Measure(Set(400, 400)).ShouldBe(AttachmentEnvelopeVerdict.WithinEnvelope);

    /// <summary>
    /// The envelope is a ceiling the set may reach. A set refused at exactly
    /// the approved number would make the published capacity one byte smaller
    /// than the value an operator declared.
    /// </summary>
    [Fact]
    public void A_set_whose_members_add_up_to_exactly_the_envelope_is_within_it()
        => Measure(Set(600, 400)).ShouldBe(AttachmentEnvelopeVerdict.WithinEnvelope);

    [Fact]
    public void A_set_one_byte_past_the_envelope_is_refused()
        => Measure(Set(600, 401)).ShouldBe(AttachmentEnvelopeVerdict.Exceeded);

    /// <summary>
    /// The sum is what the envelope bounds, so no member being over on its own
    /// says nothing: three members well inside the ceiling still leave the set
    /// outside it.
    /// </summary>
    [Fact]
    public void A_set_whose_members_are_each_inside_the_envelope_can_still_exceed_it()
        => Measure(Set(400, 400, 400)).ShouldBe(AttachmentEnvelopeVerdict.Exceeded);

    [Fact]
    public void A_set_with_exactly_the_allowed_number_of_members_is_within_the_envelope()
        => Measure(Set(1, 1, 1)).ShouldBe(AttachmentEnvelopeVerdict.WithinEnvelope);

    /// <summary>
    /// The count is bounded on its own and not through the bytes: four members
    /// of one byte are nothing at all against the envelope and are still one
    /// whole reading too many before the point of no return.
    /// </summary>
    [Fact]
    public void A_set_with_one_member_too_many_is_refused_however_small_its_members_are()
        => Measure(Set(1, 1, 1, 1)).ShouldBe(AttachmentEnvelopeVerdict.Exceeded);

    /// <summary>
    /// Two lengths whose sum does not fit a signed sixty four bit integer. A
    /// measurement that added them up would wrap to a negative number, compare
    /// as comfortably inside the ceiling and clear the set, which is the one
    /// wrong answer this must not be able to give.
    /// </summary>
    [Fact]
    public void A_set_whose_lengths_would_overflow_a_sum_is_refused_rather_than_wrapped()
        => Measure(Set(long.MaxValue, long.MaxValue)).ShouldBe(AttachmentEnvelopeVerdict.Exceeded);

    [Fact]
    public void A_measurement_of_nothing_is_refused_outright()
        => Should.Throw<ArgumentNullException>(() => Measure(null!));

    private static AttachmentEnvelopeVerdict Measure(AcceptedAttachmentSet accepted)
        => new AcceptedSetEnvelopeCheck(Options.Create(new AttachmentCapacityOptions
        {
            MaxAttachmentBytes = Envelope,
            MaxEnvelopeBytes = Envelope,
            MaxAttachmentsPerNotification = Members,
        })).Measure(accepted);

    private static AcceptedAttachmentSet Set(params long[] lengths)
        => AcceptedAttachmentSet.Of(lengths.Select((length, index) => new AcceptedAttachment
        {
            Reference = $"att_{index}",
            ContentIdentity = $"aci_{index}",
            Name = $"anexo-{index}.pdf",
            MediaType = "application/pdf",
            Length = length,
        }));
}
