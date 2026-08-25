using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.UnitTests.ContactConsent;

/// <summary>
/// The accumulation rule decides whether one refusal costs the recipient a
/// channel, which is the only irreversible-feeling consequence in this module.
/// Every case below is stated in refusal instants, because the rule is a pure
/// function of them and of the channel.
/// </summary>
public sealed class SuppressionRulesTests
{
    private static readonly DateTimeOffset FirstRefusal = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_email_refusal_suppresses_on_the_first_occurrence()
        => SuppressionRules.IsMet(ContactChannels.Email, [FirstRefusal], FirstRefusal).ShouldBeTrue();

    [Fact]
    public void An_sms_refusal_alone_does_not_suppress()
        => SuppressionRules.IsMet(ContactChannels.Sms, [FirstRefusal], FirstRefusal).ShouldBeFalse();

    [Fact]
    public void A_second_sms_refusal_inside_the_week_suppresses()
    {
        DateTimeOffset second = FirstRefusal.AddDays(2);

        SuppressionRules.IsMet(ContactChannels.Sms, [FirstRefusal, second], second).ShouldBeTrue();
    }

    [Fact]
    public void A_second_sms_refusal_outside_the_week_does_not_suppress()
    {
        // The window is measured back from the newest refusal, so an isolated
        // one ages out instead of waiting forever for a partner.
        DateTimeOffset second = FirstRefusal.AddDays(8);

        SuppressionRules.IsMet(ContactChannels.Sms, [FirstRefusal, second], second).ShouldBeFalse();
    }

    [Fact]
    public void The_edge_of_the_window_is_inside_it()
    {
        DateTimeOffset second = FirstRefusal.Add(SuppressionRules.For(ContactChannels.Sms).Window!.Value);

        SuppressionRules.IsMet(ContactChannels.Sms, [FirstRefusal, second], second).ShouldBeTrue();
    }

    [Fact]
    public void A_third_refusal_after_two_aged_out_does_not_suppress_on_the_older_pair()
    {
        DateTimeOffset second = FirstRefusal.AddDays(1);
        DateTimeOffset third = FirstRefusal.AddDays(30);

        SuppressionRules.IsMet(ContactChannels.Sms, [FirstRefusal, second, third], third).ShouldBeFalse();
    }

    [Fact]
    public void A_channel_addressed_by_number_shares_the_stricter_threshold_of_sms()
    {
        // Deliberate: a number can be refused for reasons that pass, and the
        // e-mail rule exists because a nonexistent mailbox does not come back.
        SuppressionThreshold whatsApp = SuppressionRules.For(ContactChannels.WhatsApp);

        whatsApp.ShouldBe(SuppressionRules.For(ContactChannels.Sms));
        whatsApp.Occurrences.ShouldBe(2);
        whatsApp.Window.ShouldBe(TimeSpan.FromDays(7));
    }

    [Fact]
    public void The_email_threshold_never_expires_a_refusal()
    {
        SuppressionThreshold email = SuppressionRules.For(ContactChannels.Email);

        email.Occurrences.ShouldBe(1);
        email.Window.ShouldBeNull();
    }
}
