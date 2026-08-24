using NotificationHub.Api.Modules.Dispatch.Infrastructure.Providers.Twilio;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class TwilioRequestShapeTests
{
    [Fact]
    public async Task Builds_a_programmable_messaging_request_with_the_sms_body()
    {
        TwilioOptions options = new()
        {
            Product = TwilioSmsProduct.ProgrammableMessaging,
            AccountSid = "AC123",
            FromNumber = "+5511999999999",
        };

        using HttpRequestMessage request = TwilioChannelProvider.BuildRequest(
            new SmsDeliveryTarget("+5511888888888"),
            new SmsMessage("Cotação atualizada"),
            options);
        var body = await request.Content!.ReadAsStringAsync();

        request.Method.ShouldBe(HttpMethod.Post);
        request.RequestUri!.ToString().ShouldBe(
            "2010-04-01/Accounts/AC123/Messages.json");
        body.ShouldContain("To=%2B5511888888888");
        body.ShouldContain("From=%2B5511999999999");
        body.ShouldContain("Body=Cota%C3%A7%C3%A3o+atualizada");
    }

    [Fact]
    public async Task Builds_a_verify_request_with_the_custom_code()
    {
        TwilioOptions options = new()
        {
            Product = TwilioSmsProduct.Verify,
            ServiceSid = "VA123",
        };

        using HttpRequestMessage request = TwilioChannelProvider.BuildRequest(
            new SmsDeliveryTarget("+5511888888888"),
            new SmsMessage("123456"),
            options);
        var body = await request.Content!.ReadAsStringAsync();

        request.RequestUri!.ToString().ShouldBe(
            "https://verify.twilio.com/v2/Services/VA123/Verifications");
        body.ShouldContain("To=%2B5511888888888");
        body.ShouldContain("Channel=sms");
        body.ShouldContain("CustomCode=123456");
    }
}
