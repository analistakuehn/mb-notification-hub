using NotificationHub.Api.Modules.Dispatch.Infrastructure.Resilience;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Dispatch;

/// <summary>
/// What spending the provider's budget does to one send. The store is not
/// here: what this fixes is the shape of the refusal, which is what the
/// dispatcher settles on.
/// </summary>
public sealed class RateLimitedChannelProviderTests
{
    private static DispatchRequest SomeRequest()
        => new(
            new SmsDeliveryTarget("+5511888888888"),
            new SmsMessage("Código de acesso: 123456."));

    [Fact]
    public async Task A_send_with_budget_reaches_the_provider_untouched()
    {
        var inner = new CountingProvider();
        using var limited = new RateLimitedChannelProvider(inner, new FakeBudget(allowed: true));

        ProviderResult result = await limited.SendAsync(SomeRequest(), CancellationToken.None);

        result.Outcome.ShouldBe(ProviderOutcome.Accepted);
        result.ProviderMessageId.ShouldBe(CountingProvider.MessageId);
        inner.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_send_without_budget_is_a_throttle_and_never_reaches_the_provider()
    {
        var inner = new CountingProvider();
        using var limited = new RateLimitedChannelProvider(
            inner,
            new FakeBudget(allowed: false, TimeSpan.FromSeconds(3)));

        ProviderResult result = await limited.SendAsync(SomeRequest(), CancellationToken.None);

        // The count comes first: it is the defect itself. A message that spent
        // no budget must not reach the provider anyway.
        inner.CallCount.ShouldBe(0);

        // A throttle and not a rejection: this hub decided not to call, the
        // provider said nothing, and a rejection would advance the delivery
        // plan over congestion of our own making.
        result.Outcome.ShouldBe(ProviderOutcome.Throttled);
        result.ErrorCode.ShouldBe(RateLimitedChannelProvider.RateLimitedErrorCode);
        result.RetryAfter.ShouldBe(TimeSpan.FromSeconds(3));
        result.ProviderMessageId.ShouldBeNull();

        // Nothing of the provider's own vocabulary is invented in the refusal.
        result.ErrorMessage.ShouldBeNull();
    }

    [Fact]
    public async Task The_budget_is_asked_for_the_inner_provider_identity()
    {
        var budget = new FakeBudget(allowed: true);
        using var limited = new RateLimitedChannelProvider(new CountingProvider(), budget);

        await limited.SendAsync(SomeRequest(), CancellationToken.None);

        budget.AskedFor.ShouldBe([CountingProvider.Key]);
    }

    [Fact]
    public void Exposes_the_inner_identity_unchanged()
    {
        using var limited = new RateLimitedChannelProvider(
            new CountingProvider(), new FakeBudget(allowed: true));

        limited.Channel.ShouldBeSameAs(Channel.Sms);
        limited.ProviderKey.ShouldBe(CountingProvider.Key);
    }

    [Fact]
    public void Disposing_the_decorator_disposes_what_it_wraps()
    {
        // The container tracks the instance it was handed, which is the
        // decorator; the concurrency limiter inside it holds a semaphore.
        var inner = new CountingProvider();
        var limited = new RateLimitedChannelProvider(inner, new FakeBudget(allowed: true));

        limited.Dispose();

        inner.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void The_bucket_of_a_rate_holds_one_second_of_it()
    {
        var oneSecondOfBurst = new ProviderRateLimit { PermitsPerSecond = 30 };

        oneSecondOfBurst.Capacity.ShouldBe(30);
        oneSecondOfBurst.KeyTtl.ShouldBe(TimeSpan.FromSeconds(2));

        // Falsification of the line above: the capacity follows the burst, and
        // is not the rate by another name.
        var threeSecondsOfBurst = new ProviderRateLimit { PermitsPerSecond = 30, BurstSeconds = 3 };

        threeSecondsOfBurst.Capacity.ShouldBe(90);
        threeSecondsOfBurst.KeyTtl.ShouldBe(TimeSpan.FromSeconds(4));
    }

    [Fact]
    public void A_provider_without_a_configured_rate_has_no_limit()
    {
        var options = new ProviderRateLimitOptions
        {
            PerProvider = { ["twilio"] = new ProviderRateLimit { PermitsPerSecond = 10 } },
        };

        options.For("twilio")!.PermitsPerSecond.ShouldBe(10);
        options.For("sendgrid").ShouldBeNull();

        // Configuration keys are read as a human wrote them; the provider key
        // is what an adapter calls itself.
        options.For("Twilio")!.PermitsPerSecond.ShouldBe(10);
    }

    private sealed class FakeBudget(bool allowed, TimeSpan retryAfter = default) : IProviderRateBudget
    {
        private readonly List<string> _askedFor = [];

        internal IReadOnlyList<string> AskedFor => _askedFor;

        public Task<ProviderRateDecision> TryConsumeAsync(
            string providerKey,
            CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _askedFor.Add(providerKey);
            return Task.FromResult(allowed
                ? ProviderRateDecision.Allow()
                : new ProviderRateDecision(false, retryAfter));
        }
    }

    /// <summary>
    /// Spending a token changes nothing about what a message carries, so the
    /// decorator forwards the adapter's answer and does not have one of its
    /// own. Both values are asked, because a decorator hard-wired to either of
    /// them would pass the half that matches it: false would refuse every send
    /// with a set on a deployment whose adapter carries one, and true would let
    /// one through on a deployment whose adapter does not.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Forwards_the_attachment_answer_of_the_adapter_it_wraps(bool carries)
    {
        var inner = new CountingProvider { CarriesAttachments = carries };
        using var limited = new RateLimitedChannelProvider(inner, new FakeBudget(allowed: true));

        limited.CarriesAttachments.ShouldBe(carries);
    }

    private sealed class CountingProvider : IChannelProvider, IDisposable
    {
        internal const string Key = "rate-limit-fake";
        internal const string MessageId = "rate-limit-fake-message";

        private int _callCount;

        public Channel Channel => Channel.Sms;

        public string ProviderKey => Key;

        public bool CarriesAttachments { get; init; }

        internal int CallCount => Volatile.Read(ref _callCount);

        internal bool Disposed { get; private set; }

        public Task<ProviderResult> SendAsync(
            DispatchRequest request,
            CancellationToken cancellationToken)
        {
            _ = request;
            _ = cancellationToken;
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(ProviderResult.Accepted(MessageId));
        }

        public void Dispose() => Disposed = true;
    }
}
