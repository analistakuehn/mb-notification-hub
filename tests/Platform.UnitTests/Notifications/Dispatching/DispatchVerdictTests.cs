using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Dispatching;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

public sealed class DispatchVerdictTests
{
    [Fact]
    public void Acceptance_settles_the_attempt_as_sent()
        => DispatchMessageProcessor.Decide(ProviderResult.Accepted("provider-id-1"))
            .ShouldBe(DispatchVerdict.Sent);

    [Fact]
    public void A_permanent_rejection_settles_the_attempt_as_failed()
        => DispatchMessageProcessor.Decide(ProviderResult.Rejected("http-400", null))
            .ShouldBe(DispatchVerdict.Failed);

    [Fact]
    public void Throttling_returns_the_attempt_to_the_queue()
        => DispatchMessageProcessor.Decide(
                ProviderResult.Throttled("http-429", null, TimeSpan.FromSeconds(7)))
            .ShouldBe(DispatchVerdict.Requeue);

    [Fact]
    public void An_open_circuit_returns_the_attempt_to_the_queue_because_no_call_was_taken()
        => DispatchMessageProcessor.Decide(ProviderResult.Transient("circuit-open", null))
            .ShouldBe(DispatchVerdict.Requeue);

    [Fact]
    public void A_timeout_parks_the_attempt_on_unknown_because_the_send_may_have_landed()
        => DispatchMessageProcessor.Decide(ProviderResult.Transient("timeout", null))
            .ShouldBe(DispatchVerdict.Unknown);

    [Fact]
    public void A_network_fault_parks_the_attempt_on_unknown()
        => DispatchMessageProcessor.Decide(ProviderResult.Transient("network", null))
            .ShouldBe(DispatchVerdict.Unknown);
}
