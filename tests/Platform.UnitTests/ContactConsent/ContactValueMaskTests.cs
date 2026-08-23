using NotificationHub.Api.Modules.ContactConsent.Domain;
using NotificationHub.Api.Modules.ContactConsent.Infrastructure.Privacy;

namespace NotificationHub.UnitTests.ContactConsent;

public sealed class ContactValueMaskTests
{
    [Theory]
    [InlineData("pessoa@example.com", "p*****@example.com")]
    [InlineData("MARIA.SILVA@banco.com.br", "M**********@banco.com.br")]
    [InlineData("a@example.com", "*@example.com")]
    public void An_email_keeps_the_first_character_and_the_domain(string value, string expected)
        => ContactValueMask.Apply(ContactChannels.Email, value).ShouldBe(expected);

    [Fact]
    public void An_email_without_a_domain_separator_falls_back_to_the_trailing_rule()
        => ContactValueMask.Apply(ContactChannels.Email, "sem-arroba").ShouldBe("******roba");

    [Theory]
    [InlineData(ContactChannels.Sms, "+5511999990000", "+*********0000")]
    [InlineData(ContactChannels.WhatsApp, "+5511999990000", "+*********0000")]
    [InlineData(ContactChannels.Sms, "1199990000", "******0000")]
    public void A_phone_keeps_the_last_four_digits_and_the_country_marker(
        string channel, string value, string expected)
        => ContactValueMask.Apply(channel, value).ShouldBe(expected);

    [Theory]
    [InlineData("0000")]
    [InlineData("00")]
    public void A_value_no_longer_than_the_visible_tail_masks_whole(string value)
        => ContactValueMask.Apply(ContactChannels.Sms, value)
            .ShouldBe(new string('*', value.Length));

    [Fact]
    public void The_mask_never_returns_the_value_it_received()
    {
        const string Value = "pessoa.exemplo@example.com";

        var masked = ContactValueMask.Apply(ContactChannels.Email, Value);

        masked.ShouldNotBe(Value);
        masked.ShouldNotContain("essoa.exemplo");
    }

    [Fact]
    public void The_mask_is_deterministic_for_the_same_value_and_channel()
        => ContactValueMask.Apply(ContactChannels.Sms, "+5511999990000")
            .ShouldBe(ContactValueMask.Apply(ContactChannels.Sms, "+5511999990000"));
}
