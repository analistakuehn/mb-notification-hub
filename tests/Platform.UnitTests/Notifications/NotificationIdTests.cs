using NotificationHub.Api.Modules.Notifications.Domain;

namespace NotificationHub.UnitTests.Notifications;

public sealed class NotificationIdTests
{
    [Fact]
    public void The_public_form_round_trips_back_to_the_stored_uuid()
    {
        var id = Guid.CreateVersion7();

        var publicId = NotificationId.Format(id);
        NotificationId.TryParse(publicId, out Guid parsed).ShouldBeTrue();

        parsed.ShouldBe(id);
    }

    [Fact]
    public void The_mapping_is_deterministic_over_a_known_vector()
    {
        // 128 bits set to a fixed pattern: the encoding must never drift,
        // because stored ids and public ids must keep matching forever.
        var id = new Guid("01890a5d-ac96-774b-bcce-b302099a8057");

        NotificationId.Format(id).ShouldBe("ntf_01H455VB4PEX5VSKNK084SN02Q");
    }

    [Fact]
    public void The_public_form_has_the_prefix_and_26_encoding_characters()
    {
        var publicId = NotificationId.Format(Guid.CreateVersion7());

        publicId.ShouldStartWith("ntf_");
        publicId.Length.ShouldBe(30);
    }

    [Fact]
    public void Public_ids_of_later_version7_uuids_sort_after_earlier_ones()
    {
        var earlier = NotificationId.Format(Guid.CreateVersion7(
            new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero)));
        var later = NotificationId.Format(Guid.CreateVersion7(
            new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.Zero)));

        string.CompareOrdinal(earlier, later).ShouldBeLessThan(0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01H455VB4PEX5VSKNK084SN02Q")]
    [InlineData("ntf_01H455VB4PEX5VSKNK084SN02")]
    [InlineData("ntf_01H455VB4PEX5VSKNK084SN02I")]
    [InlineData("ntf_81H455VB4PEX5VSKNK084SN02Q")]
    public void Malformed_public_forms_do_not_parse(string? value)
        => NotificationId.TryParse(value, out _).ShouldBeFalse();
}
