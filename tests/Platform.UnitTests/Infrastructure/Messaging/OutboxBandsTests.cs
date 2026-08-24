using NotificationHub.Api.Infrastructure.Messaging.Relay;

namespace NotificationHub.UnitTests.Infrastructure.Messaging;

public sealed class OutboxBandsTests
{
    [Theory]
    [InlineData("critical")]
    [InlineData("transactional")]
    [InlineData("operational")]
    [InlineData("something-unknown")]
    public void The_auth_destination_classifies_into_the_auth_band_whatever_the_stored_class(string priorityClass)
        => OutboxBands.Classify("core-auth", priorityClass).ShouldBe(OutboxBand.Auth);

    [Theory]
    [InlineData("critical")]
    [InlineData("transactional")]
    [InlineData("operational")]
    public void Dispatch_destinations_with_the_auth_suffix_classify_into_the_auth_band(string priorityClass)
        => OutboxBands.Classify("dispatch-push-auth", priorityClass).ShouldBe(OutboxBand.Auth);

    [Theory]
    [InlineData("dispatch-email-auth")]
    [InlineData("dispatch-sms-auth")]
    [InlineData("dispatch-push-auth")]
    [InlineData("dispatch-whatsapp-auth")]
    public void Every_dispatch_channel_reaches_the_auth_band_through_the_suffix(string destination)
        => OutboxBands.Classify(destination, "transactional").ShouldBe(OutboxBand.Auth);

    [Theory]
    [InlineData("contacts-auth")]
    [InlineData("core-auth-events")]
    public void The_auth_band_takes_both_halves_of_the_rule_and_not_either_one(string destination)
        => OutboxBands.Classify(destination, "operational").ShouldBe(OutboxBand.Operational);

    [Theory]
    [InlineData("core-critical", "critical", "critical")]
    [InlineData("core-transactional", "transactional", "transactional")]
    [InlineData("contacts-changed", "transactional", "transactional")]
    [InlineData("core-operational", "operational", "operational")]
    [InlineData("dispatch-push-critical", "critical", "critical")]
    [InlineData("dispatch-sms-transactional", "transactional", "transactional")]
    public void Other_destinations_classify_by_their_stored_priority_class(
        string destination, string priorityClass, string expectedBandName)
    {
        OutboxBands.TryParseName(expectedBandName, out OutboxBand expected).ShouldBeTrue();
        OutboxBands.Classify(destination, priorityClass).ShouldBe(expected);
    }

    [Fact]
    public void An_unknown_priority_class_falls_back_to_the_operational_band()
        => OutboxBands.Classify("core-critical", "misspelled").ShouldBe(OutboxBand.Operational);

    [Fact]
    public void The_drain_order_goes_auth_critical_transactional_operational()
        => OutboxBands.DrainOrder.ShouldBe(
            [OutboxBand.Auth, OutboxBand.Critical, OutboxBand.Transactional, OutboxBand.Operational]);

    [Fact]
    public void An_empty_restriction_selects_every_band()
        => OutboxBands.Restrict([]).ShouldBe(OutboxBands.DrainOrder);

    [Fact]
    public void A_restriction_selects_bands_without_reordering_the_drain()
        => OutboxBands.Restrict(["operational", "auth"]).ShouldBe([OutboxBand.Auth, OutboxBand.Operational]);

    [Fact]
    public void An_unknown_band_name_selects_nothing()
        => OutboxBands.Restrict(["bogus"]).ShouldBeEmpty();

    [Fact]
    public void Band_names_parse_to_the_bands_of_the_drain_order()
    {
        string[] names = ["auth", "critical", "transactional", "operational"];
        OutboxBand[] parsed = [.. names.Select(name =>
        {
            OutboxBands.TryParseName(name, out OutboxBand band).ShouldBeTrue();
            return band;
        })];

        parsed.ShouldBe(OutboxBands.DrainOrder);
    }

    [Fact]
    public void An_unknown_band_name_does_not_parse()
        => OutboxBands.TryParseName("Critical", out _).ShouldBeFalse();
}
