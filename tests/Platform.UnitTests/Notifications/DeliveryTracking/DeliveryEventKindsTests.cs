using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.DeliveryTracking;

namespace NotificationHub.UnitTests.Notifications.DeliveryTracking;

public sealed class DeliveryEventKindsTests
{
    [Theory]
    [InlineData(DeliveryFeedbackKind.Sent)]
    [InlineData(DeliveryFeedbackKind.Delivered)]
    [InlineData(DeliveryFeedbackKind.Read)]
    [InlineData(DeliveryFeedbackKind.Failed)]
    [InlineData(DeliveryFeedbackKind.Bounced)]
    public void Every_canonical_kind_survives_the_round_trip_through_its_durable_spelling(
        DeliveryFeedbackKind kind)
    {
        DeliveryEventKinds.TryParse(DeliveryEventKinds.From(kind), out DeliveryFeedbackKind parsed)
            .ShouldBeTrue();
        parsed.ShouldBe(kind);
    }

    [Fact]
    public void A_spelling_outside_the_durable_vocabulary_is_refused_instead_of_guessed()
        => DeliveryEventKinds.TryParse("Delivered", out _).ShouldBeFalse();

    [Fact]
    public void The_durable_spelling_of_a_kind_is_stable_text_and_not_an_ordinal()
        => DeliveryEventKinds.From(DeliveryFeedbackKind.Bounced).ShouldBe("bounced");
}
