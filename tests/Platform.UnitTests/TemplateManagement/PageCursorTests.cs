using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Http;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class PageCursorTests
{
    [Theory]
    [InlineData("auth.otp.login")]
    [InlineData("a")]
    [InlineData("order.status-changed.v2")]
    public void A_cursor_round_trips_the_last_key(string key)
    {
        string cursor = PageCursor.Encode(key);

        Result<string> decoded = PageCursor.Decode(cursor);

        decoded.IsSuccess.ShouldBeTrue();
        decoded.Value.ShouldBe(key);
    }

    [Fact]
    public void The_cursor_is_opaque_and_url_safe()
    {
        string cursor = PageCursor.Encode("auth.otp.login");

        cursor.ShouldNotContain("auth.otp.login");
        cursor.ShouldNotContain("+");
        cursor.ShouldNotContain("/");
        cursor.ShouldNotContain("=");
    }

    [Fact]
    public void A_malformed_cursor_is_reported_as_a_validation_error()
    {
        Result<string> decoded = PageCursor.Decode("not a cursor!!");

        decoded.IsFailure.ShouldBeTrue();
        decoded.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }
}
