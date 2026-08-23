using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class ChannelSelectionRuleTests
{
    [Fact]
    public async Task The_surviving_plan_keeps_the_published_order_over_the_intersection()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(channels: ["push", "sms", "email"]),
            recipient: PipelineTestData.Recipient(
                contactPoints: [new ContactPointSnapshot(Guid.NewGuid(), "sms", Verified: true)],
                devices: [new DeviceRegistration(
                    Guid.NewGuid(), "token", "android", null, DateTimeOffset.UtcNow)]));

        PolicyRuleResult result = await new ChannelSelectionRule().EvaluateAsync(
            context,
            PipelineTestData.Policy(plan: [("push", TimeSpan.FromSeconds(30)), ("sms", null)]),
            CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        context.DeliveryPlan.ShouldNotBeNull();
        context.DeliveryPlan.Select(step => step.Channel.Value).ShouldBe(["push", "sms"]);
    }

    [Fact]
    public async Task A_channel_without_contact_leaves_the_plan()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(channels: ["push", "sms"]),
            recipient: PipelineTestData.Recipient(
                contactPoints: [new ContactPointSnapshot(Guid.NewGuid(), "sms", Verified: true)],
                devices: []));

        PolicyRuleResult result = await new ChannelSelectionRule().EvaluateAsync(
            context,
            PipelineTestData.Policy(plan: [("push", TimeSpan.FromSeconds(30)), ("sms", null)]),
            CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        context.DeliveryPlan!.Select(step => step.Channel.Value).ShouldBe(["sms"]);
    }

    [Fact]
    public async Task Push_reaches_through_an_active_device_token_without_a_contact_point()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(channels: ["push"]),
            recipient: PipelineTestData.Recipient(
                contactPoints: [],
                devices: [new DeviceRegistration(
                    Guid.NewGuid(), "token", "android", null, DateTimeOffset.UtcNow)]));

        PolicyRuleResult result = await new ChannelSelectionRule().EvaluateAsync(
            context,
            PipelineTestData.Policy(plan: [("push", null)]),
            CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        context.DeliveryPlan!.Select(step => step.Channel.Value).ShouldBe(["push"]);
    }

    [Fact]
    public async Task No_surviving_channel_rejects_with_the_canonical_no_valid_contact_reason()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(channels: ["push", "sms"]),
            recipient: PipelineTestData.Recipient(contactPoints: [], devices: []));

        PolicyRuleResult result = await new ChannelSelectionRule().EvaluateAsync(
            context,
            PipelineTestData.Policy(plan: [("push", TimeSpan.FromSeconds(30)), ("sms", null)]),
            CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Reject>().Reason.ShouldBe("no-valid-contact");
        context.DeliveryPlan.ShouldBeNull();
    }

    [Fact]
    public async Task A_channel_the_published_version_ships_no_content_for_leaves_the_plan()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(channels: ["sms"]),
            recipient: PipelineTestData.Recipient(
                contactPoints: [new ContactPointSnapshot(Guid.NewGuid(), "sms", Verified: true)],
                devices: [new DeviceRegistration(
                    Guid.NewGuid(), "token", "android", null, DateTimeOffset.UtcNow)]));

        PolicyRuleResult result = await new ChannelSelectionRule().EvaluateAsync(
            context,
            PipelineTestData.Policy(plan: [("push", TimeSpan.FromSeconds(30)), ("sms", null)]),
            CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        context.DeliveryPlan!.Select(step => step.Channel.Value).ShouldBe(["sms"]);
    }
}
