using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Http;

namespace NotificationHub.UnitTests.Notifications.Queries;

public sealed class NotificationQueryCursorTests
{
    [Fact]
    public void Encode_then_decode_returns_the_same_position_down_to_the_microsecond()
    {
        DateTimeOffset createdAt = new DateTimeOffset(2026, 8, 23, 14, 5, 6, TimeSpan.Zero).AddTicks(1234560);
        var position = new NotificationQueryPosition(createdAt, Guid.CreateVersion7());

        var cursor = NotificationQueryCursor.Encode(position);

        NotificationQueryCursor.TryDecode(cursor, out NotificationQueryPosition decoded).ShouldBeTrue();
        decoded.Id.ShouldBe(position.Id);
        decoded.CreatedAt.ShouldBe(createdAt);
    }

    [Fact]
    public void Decode_normalizes_the_instant_to_utc_so_the_keyset_lands_on_the_same_row()
    {
        var createdAt = new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.FromHours(-3));
        var position = new NotificationQueryPosition(createdAt, Guid.CreateVersion7());

        NotificationQueryCursor.TryDecode(
            NotificationQueryCursor.Encode(position), out NotificationQueryPosition decoded).ShouldBeTrue();

        decoded.CreatedAt.Offset.ShouldBe(TimeSpan.Zero);
        decoded.CreatedAt.UtcDateTime.ShouldBe(createdAt.UtcDateTime);
    }

    [Fact]
    public void The_cursor_carries_the_public_identity_and_never_the_stored_uuid()
    {
        var id = Guid.CreateVersion7();
        var cursor = NotificationQueryCursor.Encode(
            new NotificationQueryPosition(DateTimeOffset.UtcNow, id));

        var decoded = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(cursor));

        decoded.ShouldContain(NotificationId.Format(id));
        decoded.ShouldNotContain(id.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-base64-url-%%%")]
    public void A_value_that_is_not_a_cursor_is_refused(string cursor)
        => NotificationQueryCursor.TryDecode(cursor, out _).ShouldBeFalse();

    [Fact]
    public void A_cursor_without_the_separator_is_refused()
    {
        var cursor = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("2026-08-23T14:05:06.123456Z"));

        NotificationQueryCursor.TryDecode(cursor, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_cursor_whose_instant_is_not_the_published_format_is_refused()
    {
        var cursor = WebEncoders.Base64UrlEncode(
            Encoding.UTF8.GetBytes($"2026-08-23|{NotificationId.Format(Guid.CreateVersion7())}"));

        NotificationQueryCursor.TryDecode(cursor, out _).ShouldBeFalse();
    }

    [Fact]
    public void A_cursor_whose_identity_is_not_the_public_form_is_refused()
    {
        var cursor = WebEncoders.Base64UrlEncode(
            Encoding.UTF8.GetBytes($"2026-08-23T14:05:06.123456Z|{Guid.CreateVersion7()}"));

        NotificationQueryCursor.TryDecode(cursor, out _).ShouldBeFalse();
    }
}
