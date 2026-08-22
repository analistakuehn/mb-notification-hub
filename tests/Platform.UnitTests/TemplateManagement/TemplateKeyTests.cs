using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class TemplateKeyTests
{
    [Theory]
    [InlineData("auth.otp.login")]
    [InlineData("kyc-doc_approved")]
    [InlineData("a")]
    [InlineData("order.status-changed.v2")]
    public void Accepts_lowercase_segmented_keys(string value)
    {
        Result<TemplateKey> result = TemplateKey.Create(value);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Value.ShouldBe(value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Auth.Otp")]
    [InlineData("auth..otp")]
    [InlineData(".auth")]
    [InlineData("auth.")]
    [InlineData("auth otp")]
    [InlineData("auth/otp")]
    public void Rejects_malformed_keys(string value)
    {
        Result<TemplateKey> result = TemplateKey.Create(value);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void Rejects_keys_longer_than_the_limit()
    {
        var value = string.Join('.', Enumerable.Repeat("segment", 40));

        TemplateKey.Create(value).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public void Trims_surrounding_whitespace_before_validating()
    {
        Result<TemplateKey> result = TemplateKey.Create("  auth.otp  ");

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Value.ShouldBe("auth.otp");
    }
}
