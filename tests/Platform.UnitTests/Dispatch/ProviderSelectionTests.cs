using NotificationHub.Api.Modules.Dispatch.Domain;
using NotificationHub.SharedKernel;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class ProviderSelectionTests
{
    private static readonly DateTimeOffset SomeInstant = new(2026, 8, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Creates_a_selection_with_the_canonical_channel_value()
    {
        Result<ProviderSelection> result = ProviderSelection.Create("EMAIL", "sendgrid", 0, SomeInstant);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.ChannelValue.ShouldBe("email");
        result.Value.ProviderKey.ShouldBe("sendgrid");
        result.Value.Priority.ShouldBe(0);
        result.Value.UpdatedAt.ShouldBe(SomeInstant);
    }

    [Fact]
    public void Trims_the_provider_key()
    {
        Result<ProviderSelection> result = ProviderSelection.Create("push", "  fcm  ", 1, SomeInstant);

        result.IsSuccess.ShouldBeTrue();
        result.Value!.ProviderKey.ShouldBe("fcm");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fax")]
    public void Rejects_an_unknown_channel(string? channel)
    {
        Result<ProviderSelection> result = ProviderSelection.Create(channel, "sendgrid", 0, SomeInstant);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Rejects_an_empty_provider_key(string? providerKey)
    {
        Result<ProviderSelection> result = ProviderSelection.Create("email", providerKey, 0, SomeInstant);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }

    [Fact]
    public void Rejects_a_negative_priority()
    {
        Result<ProviderSelection> result = ProviderSelection.Create("email", "sendgrid", -1, SomeInstant);

        result.IsFailure.ShouldBeTrue();
        result.ErrorKind.ShouldBe(ResultErrorKind.Validation);
    }
}
