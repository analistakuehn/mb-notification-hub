using System.Text.Json;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Fcm;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class FcmRequestShapeTests
{
    [Fact]
    public void Targets_the_device_token_with_the_visible_notification()
    {
        var message = new PushMessage(
            "Código de acesso",
            "Use o código para entrar",
            new Dictionary<string, string> { ["kind"] = "otp" });

        FcmSendRequest request = FcmChannelProvider.BuildRequest(
            new PushDeliveryTarget("device-token-1"), message);

        request.Message.Token.ShouldBe("device-token-1");
        request.Message.Notification.Title.ShouldBe("Código de acesso");
        request.Message.Notification.Body.ShouldBe("Use o código para entrar");
        request.Message.Data!["kind"].ShouldBe("otp");
    }

    [Fact]
    public void Omits_the_data_object_when_the_payload_is_empty()
    {
        var message = new PushMessage("Título", "Corpo", new Dictionary<string, string>());

        FcmSendRequest request = FcmChannelProvider.BuildRequest(
            new PushDeliveryTarget("device-token-2"), message);

        request.Message.Data.ShouldBeNull();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        document.RootElement.GetProperty("message").TryGetProperty("data", out _).ShouldBeFalse();
    }

    [Fact]
    public void Serializes_with_the_wire_field_names()
    {
        var message = new PushMessage(
            "Título", "Corpo", new Dictionary<string, string> { ["k"] = "v" });

        FcmSendRequest request = FcmChannelProvider.BuildRequest(
            new PushDeliveryTarget("device-token-3"), message);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        JsonElement root = document.RootElement.GetProperty("message");
        root.GetProperty("token").GetString().ShouldBe("device-token-3");
        root.GetProperty("notification").GetProperty("title").GetString().ShouldBe("Título");
        root.GetProperty("data").GetProperty("k").GetString().ShouldBe("v");
    }
}
