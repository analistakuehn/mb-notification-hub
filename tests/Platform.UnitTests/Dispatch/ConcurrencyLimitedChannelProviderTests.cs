using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

public sealed class ConcurrencyLimitedChannelProviderTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(5);

    private static DispatchRequest SomeRequest()
        => new(
            new EmailDeliveryTarget("person@example.com"),
            new EmailMessage("s", "p", "<p>h</p>", "t"));

    [Fact]
    public async Task Never_lets_more_sends_into_the_inner_provider_than_the_limit()
    {
        var inner = new BlockingProvider();
        using var limited = new ConcurrencyLimitedChannelProvider(inner, maxConcurrency: 2);

        Task<ProviderResult>[] sends = [.. Enumerable.Range(0, 5)
            .Select(_ => limited.SendAsync(SomeRequest(), CancellationToken.None))];

        await inner.WaitForInFlightAsync(2, WaitBudget);
        await Task.Delay(100);
        inner.MaxObservedInFlight.ShouldBe(2);

        inner.ReleaseAll();
        ProviderResult[] results = await Task.WhenAll(sends).WaitAsync(WaitBudget);

        results.ShouldAllBe(result => result.Outcome == ProviderOutcome.Accepted);
        inner.MaxObservedInFlight.ShouldBe(2);
    }

    [Fact]
    public async Task A_send_waiting_for_a_slot_honors_cancellation()
    {
        var inner = new BlockingProvider();
        using var limited = new ConcurrencyLimitedChannelProvider(inner, maxConcurrency: 1);

        Task<ProviderResult> occupying = limited.SendAsync(SomeRequest(), CancellationToken.None);
        await inner.WaitForInFlightAsync(1, WaitBudget);

        using var cancellation = new CancellationTokenSource();
        Task<ProviderResult> waiting = limited.SendAsync(SomeRequest(), cancellation.Token);
        await cancellation.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => waiting.WaitAsync(WaitBudget));
        inner.MaxObservedInFlight.ShouldBe(1);

        inner.ReleaseAll();
        (await occupying.WaitAsync(WaitBudget)).Outcome.ShouldBe(ProviderOutcome.Accepted);
    }

    [Fact]
    public void Exposes_the_inner_identity_unchanged()
    {
        var inner = new BlockingProvider();
        using var limited = new ConcurrencyLimitedChannelProvider(inner, maxConcurrency: 3);

        limited.Channel.ShouldBeSameAs(Channel.Email);
        limited.ProviderKey.ShouldBe("blocking-fake");
    }

    private sealed class BlockingProvider : IChannelProvider
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _inFlight;
        private int _maxObservedInFlight;

        public Channel Channel => Channel.Email;

        public string ProviderKey => "blocking-fake";

        public int MaxObservedInFlight => Volatile.Read(ref _maxObservedInFlight);

        public void ReleaseAll() => _release.TrySetResult();

        public async Task WaitForInFlightAsync(int expected, TimeSpan budget)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + budget;
            while (Volatile.Read(ref _inFlight) < expected)
            {
                if (DateTimeOffset.UtcNow > deadline)
                {
                    throw new TimeoutException(
                        $"The inner provider never reached {expected} simultaneous sends.");
                }

                await Task.Delay(10);
            }
        }

        public async Task<ProviderResult> SendAsync(
            DispatchRequest request,
            CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _inFlight);
            RecordMax(current);
            try
            {
                await _release.Task.WaitAsync(cancellationToken);
                return ProviderResult.Accepted("fake-id");
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }

        private void RecordMax(int candidate)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maxObservedInFlight);
            }
            while (candidate > observed
                && Interlocked.CompareExchange(ref _maxObservedInFlight, candidate, observed) != observed);
        }
    }
}
