using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NSubstitute;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class DedupeWindowRuleTests
{
    private static DedupeWindowRule Rule(DedupeBarrierOutcome outcome, out IDedupeBarrier barrier)
    {
        barrier = Substitute.For<IDedupeBarrier>();
        barrier.TryAcquireAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(outcome);
        return new DedupeWindowRule(barrier);
    }

    [Fact]
    public async Task Acquiring_the_barrier_allows_and_passes_the_policy_window()
    {
        DedupeWindowRule rule = Rule(DedupeBarrierOutcome.Acquired, out IDedupeBarrier barrier);
        NotificationContext context = PipelineTestData.Context();

        PolicyRuleResult result = await rule.EvaluateAsync(
            context, PipelineTestData.Policy(dedupeWindow: TimeSpan.FromSeconds(90)), CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Allow>();
        await barrier.Received(1).TryAcquireAsync(
            context.Notification.Application,
            context.Notification.TemplateKey,
            context.Notification.RecipientId,
            context.Notification.Id,
            TimeSpan.FromSeconds(90),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_held_barrier_rejects_with_the_canonical_duplicate_reason()
    {
        DedupeWindowRule rule = Rule(DedupeBarrierOutcome.Duplicate, out _);

        PolicyRuleResult result = await rule.EvaluateAsync(
            PipelineTestData.Context(), PipelineTestData.Policy(), CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Reject>().Reason.ShouldBe("duplicate-window");
    }

    [Fact]
    public async Task A_barrier_held_by_the_same_notification_allows_the_reprocessing()
    {
        DedupeWindowRule rule = Rule(DedupeBarrierOutcome.AlreadyHeld, out _);

        PolicyRuleResult result = await rule.EvaluateAsync(
            PipelineTestData.Context(), PipelineTestData.Policy(), CancellationToken.None);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        using JsonDocument evidence = JsonDocument.Parse(allow.EvidenceJson);
        evidence.RootElement.GetProperty("heldByThisNotification").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task An_unavailable_barrier_fails_open_and_records_the_fail_open_in_the_evidence()
    {
        DedupeWindowRule rule = Rule(DedupeBarrierOutcome.Unavailable, out _);

        PolicyRuleResult result = await rule.EvaluateAsync(
            PipelineTestData.Context(), PipelineTestData.Policy(), CancellationToken.None);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        using JsonDocument evidence = JsonDocument.Parse(allow.EvidenceJson);
        evidence.RootElement.GetProperty("failOpen").GetBoolean().ShouldBeTrue();
        evidence.RootElement.GetProperty("risk").GetString().ShouldBe("duplicate-possible");
    }
}
