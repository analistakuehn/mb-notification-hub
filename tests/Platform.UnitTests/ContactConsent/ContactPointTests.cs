using NotificationHub.Api.Modules.ContactConsent.Domain;

namespace NotificationHub.UnitTests.ContactConsent;

public sealed class ContactPointTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    private static ContactPoint CreatePoint()
        => ContactPoint.Declare("cus_01", ContactChannels.Email, [1, 2, 3], new string('a', 64), verified: false);

    [Fact]
    public void An_unknown_channel_is_rejected_at_declaration()
        => Should.Throw<ArgumentException>(() =>
            ContactPoint.Declare("cus_01", "pombo-correio", [1], new string('a', 64), verified: true));

    [Fact]
    public void Removing_a_point_keeps_the_row_and_stamps_the_instant()
    {
        ContactPoint point = CreatePoint();

        point.Remove(Now);

        point.IsActive.ShouldBeFalse();
        point.RemovedAt.ShouldBe(Now);
    }

    [Fact]
    public void Redeclaring_a_removed_value_revives_the_same_row()
    {
        ContactPoint point = CreatePoint();
        point.Remove(Now);

        var revived = point.Restore();

        revived.ShouldBeTrue();
        point.IsActive.ShouldBeTrue();
        point.Restore().ShouldBeFalse();
    }

    [Fact]
    public void The_verified_flag_only_reports_a_change_when_it_flips()
    {
        ContactPoint point = CreatePoint();

        point.ApplyVerified(false).ShouldBeFalse();
        point.ApplyVerified(true).ShouldBeTrue();
        point.Verified.ShouldBeTrue();
    }

    [Fact]
    public void Email_normalization_lowercases_and_trims_while_phones_only_trim()
    {
        ContactValue.Normalize(ContactChannels.Email, "  Cliente@Example.COM ")
            .ShouldBe("cliente@example.com");
        ContactValue.Normalize(ContactChannels.Sms, " +5511999990000 ")
            .ShouldBe("+5511999990000");
    }
}
