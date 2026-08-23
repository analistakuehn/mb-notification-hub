using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class ProviderErrorSanitizerTests
{
    [Fact]
    public void Masks_an_echoed_email_address()
    {
        var sanitized = ProviderErrorSanitizer.Sanitize(
            "The to address person@example.com is not a valid recipient.");

        sanitized.ShouldBe("The to address *** is not a valid recipient.");
    }

    [Fact]
    public void Masks_long_digit_runs_that_could_be_a_token_or_phone()
    {
        var sanitized = ProviderErrorSanitizer.Sanitize("Registration 5511999998888 was not found.");

        sanitized.ShouldBe("Registration *** was not found.");
    }

    [Fact]
    public void Keeps_short_numbers_such_as_status_codes()
    {
        var sanitized = ProviderErrorSanitizer.Sanitize("Upstream answered 503 twice.");

        sanitized.ShouldBe("Upstream answered 503 twice.");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Returns_null_for_missing_text(string? providerMessage)
    {
        ProviderErrorSanitizer.Sanitize(providerMessage).ShouldBeNull();
    }

    [Fact]
    public void Caps_the_text_length()
    {
        var sanitized = ProviderErrorSanitizer.Sanitize(new string('x', 2_000));

        sanitized!.Length.ShouldBe(500);
    }
}
