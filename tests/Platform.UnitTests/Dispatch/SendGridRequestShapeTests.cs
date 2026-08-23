using System.Text.Json;
using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.SendGrid;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class SendGridRequestShapeTests
{
    private static readonly EmailMessage Message = new(
        "Confirme sua operação",
        "Sua operação aguarda confirmação",
        "<html><body>Olá</body></html>",
        "Olá");

    [Fact]
    public void Addresses_the_message_to_the_delivery_target()
    {
        SendGridMailRequest request = SendGridChannelProvider.BuildRequest(
            new EmailDeliveryTarget("person@example.com"), Message, Options());

        SendGridPersonalization personalization = request.Personalizations.ShouldHaveSingleItem();
        SendGridAddress to = personalization.To.ShouldHaveSingleItem();
        to.Email.ShouldBe("person@example.com");
        request.From.Email.ShouldBe("no-reply@example.com");
        request.Subject.ShouldBe("Confirme sua operação");
    }

    [Fact]
    public void Orders_content_with_plain_text_before_html()
    {
        SendGridMailRequest request = SendGridChannelProvider.BuildRequest(
            new EmailDeliveryTarget("person@example.com"), Message, Options());

        request.Content.Count.ShouldBe(2);
        request.Content[0].Type.ShouldBe("text/plain");
        request.Content[0].Value.ShouldBe("Olá");
        request.Content[1].Type.ShouldBe("text/html");
        request.Content[1].Value.ShouldBe("<html><body>Olá</body></html>");
    }

    [Fact]
    public void Carries_the_configured_sandbox_mode()
    {
        SendGridMailRequest sandboxed = SendGridChannelProvider.BuildRequest(
            new EmailDeliveryTarget("person@example.com"), Message, Options());
        SendGridMailRequest live = SendGridChannelProvider.BuildRequest(
            new EmailDeliveryTarget("person@example.com"), Message, Options(sandbox: false));

        sandboxed.MailSettings.SandboxMode.Enable.ShouldBeTrue();
        live.MailSettings.SandboxMode.Enable.ShouldBeFalse();
    }

    [Fact]
    public void Serializes_with_the_wire_field_names_and_omits_the_absent_sender_name()
    {
        SendGridMailRequest request = SendGridChannelProvider.BuildRequest(
            new EmailDeliveryTarget("person@example.com"), Message, Options());

        using JsonDocument document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        JsonElement root = document.RootElement;

        root.GetProperty("personalizations")[0].GetProperty("to")[0]
            .GetProperty("email").GetString().ShouldBe("person@example.com");
        root.GetProperty("from").TryGetProperty("name", out _).ShouldBeFalse();
        root.GetProperty("mail_settings").GetProperty("sandbox_mode")
            .GetProperty("enable").GetBoolean().ShouldBeTrue();
        root.GetProperty("content")[0].GetProperty("type").GetString().ShouldBe("text/plain");
    }

    private static SendGridOptions Options(bool sandbox = true)
        => new()
        {
            ApiKey = "test-key",
            SenderEmail = "no-reply@example.com",
            SandboxMode = sandbox,
        };
}
