using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

namespace NotificationHub.UnitTests.Notifications.KillSwitch;

public sealed class KillSwitchReleaserTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"notificationId\":42}")]
    [InlineData("{\"notificationId\":\"not-a-guid\"}")]
    public void Invalid_claim_payload_does_not_return_a_notification_id(string payloadJson)
    {
        var parsed = KillSwitchHoldReleaser.TryReadNotificationId(payloadJson, out _);

        parsed.ShouldBeFalse();
    }

    [Fact]
    public void Valid_claim_payload_returns_the_notification_id()
    {
        var notificationId = Guid.CreateVersion7();
        var payloadJson = $$"""{"notificationId":"{{notificationId}}"}""";

        var parsed = KillSwitchHoldReleaser.TryReadNotificationId(
            payloadJson,
            out Guid parsedNotificationId);

        parsed.ShouldBeTrue();
        parsedNotificationId.ShouldBe(notificationId);
    }
}
