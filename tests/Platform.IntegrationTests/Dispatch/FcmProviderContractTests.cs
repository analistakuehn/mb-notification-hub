using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

public sealed class FcmProviderContractTests
{
    private const string TokenPath = "/oauth/token";
    private const string SendPath = "/v1/projects/test-project/messages:send";

    private const string TokenBody =
        """{"access_token":"fake-access-token","expires_in":3600,"token_type":"Bearer"}""";

    private static readonly DispatchRequest Request = new(
        new PushDeliveryTarget("device-token-1"),
        new PushMessage(
            "Código de acesso",
            "Use o código para entrar",
            new Dictionary<string, string> { ["kind"] = "otp" }));

    [Fact]
    public async Task Acquires_a_token_with_a_signed_assertion_and_sends_the_message_payload()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = request => Task.FromResult(request.Path == TokenPath
            ? new FakeProviderResponse(200, TokenBody, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:1"}""", null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "fcm");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        result.ProviderMessageId.ShouldBe("projects/test-project/messages/0:1");

        FakeProviderRequest tokenRequest = server.Requests.Single(request => request.Path == TokenPath);
        tokenRequest.ContentType.ShouldNotBeNull();
        tokenRequest.ContentType.ShouldStartWith("application/x-www-form-urlencoded");
        Dictionary<string, string> form = ParseForm(tokenRequest.Body);
        form["grant_type"].ShouldBe("urn:ietf:params:oauth:grant-type:jwt-bearer");
        form["assertion"].Split('.').Length.ShouldBe(3);

        FakeProviderRequest sendRequest = server.Requests.Single(request => request.Path == SendPath);
        sendRequest.Authorization.ShouldBe("Bearer fake-access-token");
        using JsonDocument payload = JsonDocument.Parse(sendRequest.Body);
        JsonElement message = payload.RootElement.GetProperty("message");
        message.GetProperty("token").GetString().ShouldBe("device-token-1");
        message.GetProperty("notification").GetProperty("title").GetString().ShouldBe("Código de acesso");
        message.GetProperty("data").GetProperty("kind").GetString().ShouldBe("otp");
    }

    [Fact]
    public async Task Reuses_the_cached_token_across_sends()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = request => Task.FromResult(request.Path == TokenPath
            ? new FakeProviderResponse(200, TokenBody, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:2"}""", null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "fcm");

        await provider.SendAsync(Request, CancellationToken.None);
        await provider.SendAsync(Request, CancellationToken.None);

        server.Requests.Count(request => request.Path == TokenPath).ShouldBe(1);
        server.Requests.Count(request => request.Path == SendPath).ShouldBe(2);
    }

    [Fact]
    public async Task An_unregistered_device_token_maps_to_a_permanent_rejection_with_the_provider_code()
    {
        const string body = """
            {"error":{"code":404,"message":"Requested entity was not found.","status":"NOT_FOUND",
            "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}
            """;
        ProviderResult result = await SendWithProviderAnswerAsync(404, body);

        result.Outcome.ShouldBe(ProviderOutcome.Rejected);
        result.ErrorCode.ShouldBe("UNREGISTERED");
    }

    [Fact]
    public async Task An_invalid_argument_maps_to_a_permanent_rejection()
    {
        const string body =
            """{"error":{"code":400,"message":"The registration token is not a valid FCM registration token","status":"INVALID_ARGUMENT"}}""";
        ProviderResult result = await SendWithProviderAnswerAsync(400, body);

        result.Outcome.ShouldBe(ProviderOutcome.Rejected);
        result.ErrorCode.ShouldBe("INVALID_ARGUMENT");
    }

    [Fact]
    public async Task Exhausted_quota_maps_to_throttled()
    {
        const string body = """
            {"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED",
            "details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"QUOTA_EXCEEDED"}]}}
            """;
        ProviderResult result = await SendWithProviderAnswerAsync(429, body);

        result.Outcome.ShouldBe(ProviderOutcome.Throttled);
        result.ErrorCode.ShouldBe("QUOTA_EXCEEDED");
    }

    [Fact]
    public async Task A_server_fault_maps_to_transient_with_the_rpc_status()
    {
        const string body =
            """{"error":{"code":503,"message":"The service is currently unavailable.","status":"UNAVAILABLE"}}""";
        ProviderResult result = await SendWithProviderAnswerAsync(503, body);

        result.Outcome.ShouldBe(ProviderOutcome.TransientError);
        result.ErrorCode.ShouldBe("UNAVAILABLE");
    }

    [Fact]
    public async Task A_failing_token_endpoint_maps_the_send_to_transient()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = request => Task.FromResult(request.Path == TokenPath
            ? new FakeProviderResponse(500, null, null)
            : new FakeProviderResponse(200, """{"name":"projects/test-project/messages/0:3"}""", null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "fcm");

        ProviderResult result = await provider.SendAsync(Request, CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.TransientError);
        result.ErrorCode.ShouldBe("auth-token");
        server.Requests.ShouldAllBe(request => request.Path == TokenPath);
    }

    private static async Task<ProviderResult> SendWithProviderAnswerAsync(int statusCode, string body)
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        server.Handler = request => Task.FromResult(request.Path == TokenPath
            ? new FakeProviderResponse(200, TokenBody, null)
            : new FakeProviderResponse(statusCode, body, null));
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            Settings(server.BaseAddress));
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "fcm");

        return await provider.SendAsync(Request, CancellationToken.None);
    }

    private static Dictionary<string, string?> Settings(Uri baseAddress)
    {
        using var rsa = RSA.Create(2048);
        return new Dictionary<string, string?>
        {
            ["Modules:Dispatch:Providers:Fcm:BaseAddress"] = baseAddress.ToString(),
            ["Modules:Dispatch:Providers:Fcm:ProjectId"] = "test-project",
            ["Modules:Dispatch:Providers:Fcm:ServiceAccountEmail"] = "svc@test-project.iam.gserviceaccount.com",
            ["Modules:Dispatch:Providers:Fcm:ServiceAccountPrivateKeyPem"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["Modules:Dispatch:Providers:Fcm:TokenUri"] = new Uri(baseAddress, TokenPath).ToString(),
        };
    }

    private static Dictionary<string, string> ParseForm(string body)
        => body.Split('&')
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(
                parts => WebUtility.UrlDecode(parts[0]),
                parts => WebUtility.UrlDecode(parts[1]),
                StringComparer.Ordinal);
}
