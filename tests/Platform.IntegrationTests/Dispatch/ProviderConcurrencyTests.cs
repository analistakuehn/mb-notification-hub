using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;

namespace NotificationHub.IntegrationTests.Dispatch;

public sealed class ProviderConcurrencyTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private static readonly DispatchRequest Request = new(
        new EmailDeliveryTarget("person@example.com"),
        new EmailMessage("Assunto", "Pre", "<p>Olá</p>", "Olá"));

    [Fact]
    public async Task The_registered_provider_never_exceeds_its_configured_concurrency()
    {
        await using FakeProviderServer server = await FakeProviderServer.StartAsync();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Handler = async _ =>
        {
            await release.Task;
            return new FakeProviderResponse(202, null, null);
        };
        await using ServiceProvider services = DispatchTestServices.BuildProviderHost(
            new Dictionary<string, string?>
            {
                ["Modules:Dispatch:Providers:SendGrid:BaseAddress"] = server.BaseAddress.ToString(),
                ["Modules:Dispatch:Providers:SendGrid:ApiKey"] = "sg-test-key",
                ["Modules:Dispatch:Providers:SendGrid:SenderEmail"] = "no-reply@example.com",
                ["Modules:Dispatch:Providers:SendGrid:MaxConcurrency"] = "2",
                ["Modules:Dispatch:Providers:SendGrid:TimeoutSeconds"] = "30",
            });
        IChannelProvider provider = DispatchTestServices.ResolveProviderByKey(services, "sendgrid");

        Task<ProviderResult>[] sends = [.. Enumerable.Range(0, 6)
            .Select(_ => provider.SendAsync(Request, CancellationToken.None))];

        await WaitForRequestCountAsync(server, 2);
        await Task.Delay(150);
        server.RequestCount.ShouldBe(2);

        release.SetResult();
        ProviderResult[] results = await Task.WhenAll(sends).WaitAsync(WaitBudget);

        results.ShouldAllBe(result => result.Outcome == ProviderOutcome.Accepted);
        server.RequestCount.ShouldBe(6);
        server.MaxObservedConcurrency.ShouldBe(2);
    }

    private static async Task WaitForRequestCountAsync(FakeProviderServer server, int expected)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + WaitBudget;
        while (server.RequestCount < expected)
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The fake provider never received {expected} simultaneous requests.");
            }

            await Task.Delay(10);
        }
    }
}
