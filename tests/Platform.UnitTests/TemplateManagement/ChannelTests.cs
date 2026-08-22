using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class ChannelTests
{
    [Theory]
    [InlineData("email")]
    [InlineData("sms")]
    [InlineData("push")]
    [InlineData("whatsapp")]
    public void Accepts_every_supported_channel(string value)
    {
        Result<Channel> result = Channel.Create(value);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.Value.ShouldBe(value);
    }

    [Fact]
    public void Matching_is_case_insensitive_and_yields_the_canonical_instance()
    {
        Result<Channel> result = Channel.Create("EMAIL");

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBeSameAs(Channel.Email);
    }

    [Theory]
    [InlineData("")]
    [InlineData("fax")]
    [InlineData("e-mail")]
    public void Rejects_unknown_channels(string value)
    {
        Result<Channel> result = Channel.Create(value);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }
}
