using NotificationHub.Api.Modules.Notifications.Features.Fallback;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Dispatching;

public sealed class FallbackPlanStepTests
{
    [Fact]
    public void The_step_after_the_failed_channel_is_the_next_one()
    {
        DeliveryPlanStep? next = FallbackRequestHandler.NextStep(
            Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)), failedChannel: "push");

        next.ShouldNotBeNull();
        next.Channel.Value.ShouldBe("email");
        next.Timeout.ShouldBeNull();
    }

    [Fact]
    public void The_last_step_of_the_plan_has_no_successor()
        => FallbackRequestHandler.NextStep(
                Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)), failedChannel: "email")
            .ShouldBeNull();

    [Fact]
    public void A_channel_outside_the_published_plan_has_no_successor()
        => FallbackRequestHandler.NextStep(
                Plan(("push", TimeSpan.FromSeconds(30)), ("email", null)), failedChannel: "sms")
            .ShouldBeNull();

    private static DeliveryPlanStep[] Plan(params (string Channel, TimeSpan? Timeout)[] steps)
        => [.. steps.Select(step =>
            new DeliveryPlanStep(Channel.Create(step.Channel).Value!, step.Timeout))];
}
