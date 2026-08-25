using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;
using NotificationHub.Api.Modules.Notifications.Infrastructure.KillSwitch;

namespace NotificationHub.UnitTests.Notifications.KillSwitch;

/// <summary>
/// The window that decides whether a channel stopped being deliverable. It is
/// the arithmetic behind a global stop, so every boundary here is asserted by
/// what the observer answers, never by what it holds.
/// </summary>
public sealed class ChannelCircuitObserverTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    [Fact]
    public void One_observation_never_crosses_the_window()
    {
        var time = new ManualTimeProvider();
        var observer = new ChannelCircuitObserver(time);

        observer.ObserveOpenCircuit("sms", Window).ShouldBeFalse();
    }

    [Fact]
    public void The_window_is_still_open_one_second_before_it_ends()
    {
        var time = new ManualTimeProvider();
        var observer = new ChannelCircuitObserver(time);
        observer.ObserveOpenCircuit("sms", Window);

        time.Advance(Window - TimeSpan.FromSeconds(1));

        observer.ObserveOpenCircuit("sms", Window).ShouldBeFalse();
    }

    [Fact]
    public void An_observation_past_the_window_crosses_it_once()
    {
        var time = new ManualTimeProvider();
        var observer = new ChannelCircuitObserver(time);
        observer.ObserveOpenCircuit("sms", Window);

        time.Advance(Window + TimeSpan.FromSeconds(1));

        observer.ObserveOpenCircuit("sms", Window).ShouldBeTrue();

        // A stop already decided must not be decided again on every message
        // that follows it: the consequence is global and the store would be
        // asked to repeat a transition it already made.
        time.Advance(TimeSpan.FromHours(1));
        observer.ObserveOpenCircuit("sms", Window).ShouldBeFalse();
    }

    [Fact]
    public void A_call_that_reached_the_provider_starts_the_window_over()
    {
        var time = new ManualTimeProvider();
        var observer = new ChannelCircuitObserver(time);
        observer.ObserveOpenCircuit("sms", Window);

        time.Advance(Window - TimeSpan.FromSeconds(30));
        observer.ObserveProviderAnswered("sms");
        observer.ObserveOpenCircuit("sms", Window);

        // Nine and a half minutes of the first streak plus one of the second
        // is not ten minutes of anything: the streak is continuous or it is
        // not a streak.
        time.Advance(TimeSpan.FromMinutes(1));
        observer.ObserveOpenCircuit("sms", Window).ShouldBeFalse();

        time.Advance(Window);
        observer.ObserveOpenCircuit("sms", Window).ShouldBeTrue();
    }

    [Fact]
    public void Each_channel_carries_its_own_window()
    {
        var time = new ManualTimeProvider();
        var observer = new ChannelCircuitObserver(time);
        observer.ObserveOpenCircuit("sms", Window);

        time.Advance(Window + TimeSpan.FromSeconds(1));

        observer.ObserveOpenCircuit("email", Window).ShouldBeFalse();
        observer.ObserveOpenCircuit("sms", Window).ShouldBeTrue();
    }

    [Fact]
    public void Only_the_open_circuit_feeds_the_window()
    {
        // Every other verdict came back after the breaker let the call
        // through, which is the proof that the circuit is closed.
        DispatchMessageProcessor.CircuitSignalOf(
                ProviderResult.Transient(DispatchMessageProcessor.CircuitOpenErrorCode, null))
            .ShouldBe(ChannelCircuitSignal.CircuitOpen);
        DispatchMessageProcessor.CircuitSignalOf(ProviderResult.Transient("timeout", null))
            .ShouldBe(ChannelCircuitSignal.ProviderAnswered);
        DispatchMessageProcessor.CircuitSignalOf(ProviderResult.Accepted("SM-1"))
            .ShouldBe(ChannelCircuitSignal.ProviderAnswered);
        DispatchMessageProcessor.CircuitSignalOf(ProviderResult.Rejected("21610", null))
            .ShouldBe(ChannelCircuitSignal.ProviderAnswered);
        DispatchMessageProcessor.CircuitSignalOf(ProviderResult.Throttled("429", null, null))
            .ShouldBe(ChannelCircuitSignal.ProviderAnswered);

        // A send this hub held back on its own rate never reached the
        // provider, so it says nothing about the provider either way.
        DispatchMessageProcessor.CircuitSignalOf(
                ProviderResult.Throttled(DispatchMessageProcessor.RateLimitedErrorCode, null, null))
            .ShouldBe(ChannelCircuitSignal.None);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        internal void Advance(TimeSpan duration) => _utcNow += duration;
    }
}
