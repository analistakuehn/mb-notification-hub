using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

public sealed class ProviderCircuitBreakerTests
{
    private const string SendGridPath = "/v3/mail/send";

    private static readonly DispatchRequest EmailRequest = new(
        new EmailDeliveryTarget("person@example.com"),
        new EmailMessage("Assunto", "Pre", "<p>Olá</p>", "Olá"));

    private static readonly DispatchRequest PushRequest = new(
        new PushDeliveryTarget("device-token-1"),
        new PushMessage("Título", "Corpo", new Dictionary<string, string>()));

    [Fact]
    public async Task The_circuit_opens_after_repeated_failures_and_later_sends_fail_fast()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(500, null, null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            SendGridSettings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        var results = new List<ProviderResult>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            results.Add(await provider.SendAsync(EmailRequest, CancellationToken.None));
        }

        results.ShouldAllBe(result => result.Outcome == ProviderOutcome.TransientError);
        results[0].ErrorCode.ShouldBe("http-500");
        results[1].ErrorCode.ShouldBe("http-500");
        results[^1].ErrorCode.ShouldBe("circuit-open");

        var callsThatReachedTheProvider = server.RequestCount;
        callsThatReachedTheProvider.ShouldBeLessThan(5);

        ProviderResult afterOpen = await provider.SendAsync(EmailRequest, CancellationToken.None);
        afterOpen.ErrorCode.ShouldBe("circuit-open");
        server.RequestCount.ShouldBe(callsThatReachedTheProvider);
    }

    [Fact]
    public async Task An_open_email_circuit_does_not_touch_the_push_provider()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = request => Task.FromResult(request.Path switch
        {
            SendGridPath => new FakeProviderResponse(500, null, null),
            "/oauth/token" => new FakeProviderResponse(
                200,
                """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""",
                null),
            _ => new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:9"}""", null),
        });
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            SendGridSettings(server.BaseAddress).Concat(FcmSettings(server.BaseAddress)));
        IChannelProvider email = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");
        IChannelProvider push = DispatchTestServices.ResolveProviderByKey(services, "fcm");

        for (var attempt = 0; attempt < 4; attempt++)
        {
            await email.SendAsync(EmailRequest, CancellationToken.None);
        }

        (await email.SendAsync(EmailRequest, CancellationToken.None))
            .ErrorCode.ShouldBe("circuit-open");
        (await push.SendAsync(PushRequest, CancellationToken.None))
            .Outcome.ShouldBe(ProviderOutcome.Accepted);
    }

    private static Dictionary<string, string?> SendGridSettings(Uri baseAddress)
        => new()
        {
            ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = baseAddress.ToString(),
            ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-test-key",
            ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = "no-reply@example.com",
            ["Modules:Dispatch:Providers:SendGrid:CircuitBreaker:MinimumThroughput"] = "2",
            ["Modules:Dispatch:Providers:SendGrid:CircuitBreaker:FailureRatio"] = "0.5",
            ["Modules:Dispatch:Providers:SendGrid:CircuitBreaker:BreakDurationSeconds"] = "60",
        };

    private static Dictionary<string, string?> FcmSettings(Uri baseAddress)
    {
        using var rsa = RSA.Create(2048);
        return new Dictionary<string, string?>
        {
            ["Modules:Dispatch:Providers:Fcm:BaseAddress"] = baseAddress.ToString(),
            ["Modules:Dispatch:Providers:Fcm:ProjectId"] = "test-project",
            ["Modules:Dispatch:Providers:Fcm:ServiceAccountEmail"] = "svc@test-project.iam.gserviceaccount.com",
            ["Modules:Dispatch:Providers:Fcm:ServiceAccountPrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["Modules:Dispatch:Providers:Fcm:TokenUri"] = new Uri(baseAddress, "/oauth/token").ToString(),
        };
    }
}
