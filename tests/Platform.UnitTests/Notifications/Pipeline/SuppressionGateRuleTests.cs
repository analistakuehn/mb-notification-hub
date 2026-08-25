using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NSubstitute;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class SuppressionGateRuleTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid SmsPoint = Guid.NewGuid();
    private static readonly Guid EmailPoint = Guid.NewGuid();
    private static readonly Guid SecondEmailPoint = Guid.NewGuid();

    private static readonly ContactPointSnapshot[] TwoPoints =
    [
        new(SmsPoint, "sms", Verified: true),
        new(EmailPoint, "email", Verified: true),
    ];

    [Fact]
    public async Task Nothing_suppressed_allows_and_still_states_what_it_looked_at()
    {
        PolicyRuleResult result = await Evaluate(TwoPoints, [], ["sms", "email"]);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        using var evidence = JsonDocument.Parse(allow.EvidenceJson);
        evidence.RootElement.GetProperty("suppressed").EnumerateArray().ShouldBeEmpty();
        Channels(evidence, "surviving").ShouldBe(["email", "sms"]);
    }

    [Fact]
    public async Task A_suppressed_channel_leaves_the_remaining_set_and_the_others_survive()
    {
        PolicyRuleResult result = await Evaluate(
            TwoPoints, [Suppression(EmailPoint, "email")], ["sms", "email"]);

        PolicyRuleResult.FilterChannels filter = result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        filter.Channels.Select(channel => channel.Value).ShouldBe(["sms"]);
        using var evidence = JsonDocument.Parse(filter.EvidenceJson);
        Channels(evidence, "suppressed").ShouldBe(["email"]);
        Channels(evidence, "surviving").ShouldBe(["sms"]);
    }

    [Fact]
    public async Task Every_channel_suppressed_rejects_with_the_canonical_reason()
    {
        PolicyRuleResult result = await Evaluate(
            TwoPoints,
            [Suppression(EmailPoint, "email"), Suppression(SmsPoint, "sms")],
            ["sms", "email"]);

        result.ShouldBeOfType<PolicyRuleResult.Reject>().Reason.ShouldBe("channel-suppressed");
    }

    [Fact]
    public async Task A_channel_with_a_second_reachable_address_survives_the_suppression_of_one()
    {
        // The protection exists for the recipient: taking a channel away over
        // one dead address while another one works would be a delivery failure
        // dressed as a safeguard.
        ContactPointSnapshot[] twoEmails =
        [
            new(EmailPoint, "email", Verified: true),
            new(SecondEmailPoint, "email", Verified: true),
        ];

        PolicyRuleResult result = await Evaluate(
            twoEmails, [Suppression(EmailPoint, "email")], ["email"]);

        result.ShouldBeOfType<PolicyRuleResult.Allow>();
    }

    [Fact]
    public async Task A_bounded_suppression_already_over_does_not_block_the_channel()
    {
        SuppressionState expired = new(
            EmailPoint, "email", "hard-bounce", Now.AddDays(-10), Now.AddDays(-1));

        PolicyRuleResult result = await Evaluate(TwoPoints, [expired], ["sms", "email"]);

        result.ShouldBeOfType<PolicyRuleResult.Allow>();
    }

    [Fact]
    public async Task Push_carries_no_contact_point_and_is_never_suppressed_by_this_rule()
    {
        PolicyRuleResult result = await Evaluate(
            TwoPoints,
            [Suppression(EmailPoint, "email"), Suppression(SmsPoint, "sms")],
            ["push", "sms", "email"]);

        PolicyRuleResult.FilterChannels filter = result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        filter.Channels.Select(channel => channel.Value).ShouldBe(["push"]);
    }

    /// <summary>
    /// The position is the decision, not the rule alone: after consent, so the
    /// stronger refusal is the one recorded, and before the silence window, so
    /// nothing is deferred for hours only to be rejected in the morning.
    /// </summary>
    [Fact]
    public void The_rule_runs_after_the_consent_gate_and_before_the_silence_window()
    {
        using ServiceProvider provider = RuleContainer();

        var order = CoreWorkerRole.RulesInOrder(provider).Select(rule => rule.Name).ToArray();

        order.ShouldBe(
            ["ConsentGate", "SuppressionGate", "QuietHours", "DedupeWindow", "ChannelSelection"]);
    }

    private static ServiceProvider RuleContainer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(new FrozenTimeProvider(Now));
        services.AddSingleton(Substitute.For<IDedupeBarrier>());
        services.AddScoped<ConsentGateRule>();
        services.AddScoped<SuppressionGateRule>();
        services.AddScoped<QuietHoursRule>();
        services.AddScoped<DedupeWindowRule>();
        services.AddScoped<ChannelSelectionRule>();
        return services.BuildServiceProvider();
    }

    private static SuppressionState Suppression(Guid contactPointId, string channel)
        => new(contactPointId, channel, "hard-bounce", Now.AddDays(-1), null);

    private static Task<PolicyRuleResult> Evaluate(
        IReadOnlyList<ContactPointSnapshot> contactPoints,
        IReadOnlyList<SuppressionState> suppressions,
        IEnumerable<string> remainingChannels)
        => new SuppressionGateRule(new FrozenTimeProvider(Now)).EvaluateAsync(
            PipelineTestData.Context(
                recipient: PipelineTestData.Recipient(
                    contactPoints: contactPoints, suppressions: suppressions),
                remainingChannels: remainingChannels),
            PipelineTestData.Policy(),
            CancellationToken.None);

    private static string[] Channels(JsonDocument evidence, string member)
        => [.. evidence.RootElement.GetProperty(member)
            .EnumerateArray()
            .Select(element => element.GetString()!)];
}
