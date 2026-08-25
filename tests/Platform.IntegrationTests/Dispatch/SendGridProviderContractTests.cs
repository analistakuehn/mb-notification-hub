using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

public sealed class SendGridProviderContractTests
{
    private static readonly DispatchRequest Request = new(
        new EmailDeliveryTarget("person@example.com"),
        new EmailMessage("Confirme sua operação", "Aguardando confirmação", "<p>Olá</p>", "Olá"));

    [Fact]
    public async Task Sends_the_mail_payload_with_bearer_auth_and_maps_acceptance()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(
            202, null, new Dictionary<string, string> { ["X-Message-Id"] = "msg-42" }));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        result.ProviderMessageId.ShouldBe("msg-42");

        FakeProviderRequest captured = server.Requests.ShouldHaveSingleItem();
        captured.Method.ShouldBe("POST");
        captured.Path.ShouldBe("/v3/mail/send");
        captured.Authorization.ShouldBe("Bearer sg-test-key");
        using var payload = JsonDocument.Parse(captured.Body);
        JsonElement root = payload.RootElement;
        root.GetProperty("personalizations")[0].GetProperty("to")[0]
            .GetProperty("email").GetString().ShouldBe("person@example.com");
        root.GetProperty("from").GetProperty("email").GetString().ShouldBe("no-reply@example.com");
        root.GetProperty("subject").GetString().ShouldBe("Confirme sua operação");
        root.GetProperty("content")[0].GetProperty("type").GetString().ShouldBe("text/plain");
        root.GetProperty("content")[1].GetProperty("type").GetString().ShouldBe("text/html");
        root.GetProperty("mail_settings").GetProperty("sandbox_mode")
            .GetProperty("enable").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task A_definitive_rejection_maps_to_rejected_with_a_sanitized_provider_message()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(
            400,
            """{"errors":[{"message":"the to address person@example.com is invalid","field":"personalizations.0.to.0.email"}]}""",
            null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Rejected);
        result.ErrorCode.ShouldBe("http-400");
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage.ShouldContain("personalizations.0.to.0.email");
        result.ErrorMessage.ShouldNotContain("person@example.com");
    }

    [Fact]
    public async Task Throttling_maps_to_throttled_with_the_retry_after_hint()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(
            429,
            """{"errors":[{"message":"too many requests","field":null}]}""",
            new Dictionary<string, string> { ["Retry-After"] = "7" }));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Throttled);
        result.ErrorCode.ShouldBe("http-429");
        result.RetryAfter.ShouldBe(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task A_server_fault_maps_to_transient()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = _ => Task.FromResult(new FakeProviderResponse(500, null, null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.TransientError);
        result.ErrorCode.ShouldBe("http-500");
    }

    [Fact]
    public async Task A_provider_that_never_answers_maps_to_a_transient_timeout()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            return new FakeProviderResponse(202, null, null);
        };
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress, timeoutSeconds: 1));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.TransientError);
        result.ErrorCode.ShouldBe("timeout");
    }

    private static Dictionary<string, string?> Settings(Uri baseAddress, int timeoutSeconds = 5)
        => new()
        {
            ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = baseAddress.ToString(),
            ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-test-key",
            ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = "no-reply@example.com",
            ["Modules:Dispatch:Providers:SendGrid:TimeoutSeconds"] =
                timeoutSeconds.ToString(CultureInfo.InvariantCulture),
        };
}
