using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

public sealed class TwilioProviderContractTests
{
    private static readonly DispatchRequest Request = new(
        new SmsDeliveryTarget("+5511888888888"),
        new SmsMessage("123456"));

    [Fact]
    public async Task Sends_a_programmable_sms_with_basic_auth_and_maps_acceptance()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(
            201,
            """{"sid":"SM0123456789abcdef0123456789abcdef"}""",
            null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "twilio");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        result.ProviderMessageId.ShouldBe("SM0123456789abcdef0123456789abcdef");

        FakeProviderRequest captured = server.Requests.ShouldHaveSingleItem();
        captured.Path.ShouldBe("/2010-04-01/Accounts/AC-test/Messages.json");
        captured.Authorization.ShouldBe(
            $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes("AC-test:auth-token"))}");
        Dictionary<string, string> form = ParseForm(captured.Body);
        form["To"].ShouldBe("+5511888888888");
        form["From"].ShouldBe("+5511999999999");
        form["Body"].ShouldBe("123456");
    }

    [Fact]
    public async Task Maps_provider_throttling_to_a_retryable_result()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(
            429,
            """{"code":20429,"message":"Too many requests"}""",
            new Dictionary<string, string> { ["Retry-After"] = "6" }));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "twilio");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Throttled);
        result.ErrorCode.ShouldBe("20429");
        result.RetryAfter.ShouldBe(TimeSpan.FromSeconds(6));
    }

    private static Dictionary<string, string?> Settings(Uri baseAddress)
        => new()
        {
            ["Modules:Dispatch:Providers:Twilio:BaseAddress"] = baseAddress.ToString(),
            ["Modules:Dispatch:Providers:Twilio:Product"] = "ProgrammableMessaging",
            ["Modules:Dispatch:Providers:Twilio:AuthenticationMode"] = "AuthToken",
            ["Modules:Dispatch:Providers:Twilio:AccountSid"] = "AC-test",
            ["Modules:Dispatch:Providers:Twilio:CredentialSecret"] = "auth-token",
            ["Modules:Dispatch:Providers:Twilio:FromNumber"] = "+5511999999999",
            ["Modules:Dispatch:Providers:Twilio:AllowedCountryPrefixes:0"] = "+55",
        };

    private static Dictionary<string, string> ParseForm(string body)
        => body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => WebUtility.UrlDecode(parts[1]),
                StringComparer.Ordinal);
}
