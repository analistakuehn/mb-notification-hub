using NotificationHub.Api.Modules.TemplateManagement.Domain;

namespace NotificationHub.UnitTests.TemplateManagement;

public sealed class PolicyRuleContractTests
{
    private static readonly ClassPolicyDefinition Policy = ClassPolicyDefinition.Read("""
        {
          "schemaVersion": 1,
          "channelsAllowed": ["push", "sms"],
          "deliveryPlan": [{ "channel": "push", "timeout": "30s" }, { "channel": "sms" }],
          "defaultTtl": "300s",
          "dedupeWindow": "60s"
        }
        """).Value!;

    [Fact]
    public async Task A_rule_reports_an_allow_that_still_carries_evidence()
    {
        var rule = new FakeRule("consent-gate", new PolicyRuleResult.Allow
        {
            EvidenceJson = """{"basis":"contractual"}""",
        });

        PolicyRuleResult result = await rule.EvaluateAsync(
            new FakeContext("recipient-1"), Policy, CancellationToken.None);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        allow.EvidenceJson.ShouldBe("""{"basis":"contractual"}""");
        rule.Name.ShouldBe("consent-gate");
    }

    [Fact]
    public async Task A_filter_result_carries_the_surviving_channel_set()
    {
        var rule = new FakeRule("channel-selection", new PolicyRuleResult.FilterChannels(
            new HashSet<Channel> { Channel.Push })
        {
            EvidenceJson = """{"removed":["sms"]}""",
        });

        PolicyRuleResult result = await rule.EvaluateAsync(
            new FakeContext("recipient-1"), Policy, CancellationToken.None);

        PolicyRuleResult.FilterChannels filter = result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        filter.Channels.ShouldBe([Channel.Push]);
        filter.EvidenceJson.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_defer_result_carries_the_release_instant()
    {
        var releaseAt = new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero);
        var rule = new FakeRule("quiet-hours", new PolicyRuleResult.Defer(releaseAt)
        {
            EvidenceJson = """{"window":"22:00-07:00"}""",
        });

        PolicyRuleResult result = await rule.EvaluateAsync(
            new FakeContext("recipient-1"), Policy, CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Defer>().ReleaseAt.ShouldBe(releaseAt);
    }

    [Fact]
    public async Task A_reject_result_carries_a_stable_reason()
    {
        var rule = new FakeRule("dedupe-window", new PolicyRuleResult.Reject("duplicate-request")
        {
            EvidenceJson = """{"windowSeconds":60}""",
        });

        PolicyRuleResult result = await rule.EvaluateAsync(
            new FakeContext("recipient-1"), Policy, CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Reject>().Reason.ShouldBe("duplicate-request");
    }

    /// <summary>Stand-in for the pipeline context the Core closes the contract with.</summary>
    private sealed record FakeContext(string RecipientId);

    private sealed class FakeRule(string name, PolicyRuleResult result) : IPolicyRule<FakeContext>
    {
        public string Name => name;

        public Task<PolicyRuleResult> EvaluateAsync(
            FakeContext context,
            ClassPolicyDefinition policy,
            CancellationToken cancellationToken)
            => Task.FromResult(result);
    }
}
