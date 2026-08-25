using System.Reflection;
using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Auditing;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NSubstitute;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

/// <summary>
/// The completeness check of the disclosable-evidence allow-list. It runs the
/// real rules, collects the evidence keys they actually emit, and fails when a
/// key is not covered. Without it the allow-list becomes a silent hole in the
/// audit surface: a rule grows a field, the projection quietly drops it, and
/// nobody notices until an auditor asks for something that was never served.
/// </summary>
public sealed class PolicyEvidenceProjectionTests
{
    private const string ConsentPurpose = "marketing";

    private static readonly DateTimeOffset InsideQuietWindowUtc =
        new(2026, 8, 23, 2, 30, 0, TimeSpan.Zero);

    private static readonly QuietHoursWindow QuietWindow = new(new TimeOnly(22, 0), new TimeOnly(8, 0));

    [Fact]
    public void Every_policy_rule_of_the_pipeline_declares_its_disclosable_keys()
    {
        var declared = PolicyEvidenceProjection.AllowedKeysByRule.Keys.ToHashSet(StringComparer.Ordinal);

        var undeclared = DiscoveredRuleNames()
            .Where(rule => !declared.Contains(rule))
            .ToArray();

        undeclared.ShouldBeEmpty();
    }

    [Fact]
    public void The_allow_list_declares_no_rule_the_pipeline_does_not_have()
    {
        var discovered = DiscoveredRuleNames().ToHashSet(StringComparer.Ordinal);

        var orphaned = PolicyEvidenceProjection.AllowedKeysByRule.Keys
            .Where(rule => !discovered.Contains(rule))
            .ToArray();

        orphaned.ShouldBeEmpty();
    }

    [Fact]
    public async Task No_rule_emits_an_evidence_key_the_allow_list_does_not_cover()
    {
        IReadOnlyDictionary<string, IReadOnlySet<string>> emitted = await EmittedKeysByRuleAsync();

        // The exercisers must reach every rule: a rule nobody runs would pass
        // this check by never emitting anything, which is the tautology the
        // whole test exists to avoid.
        emitted.Keys.Order(StringComparer.Ordinal)
            .ShouldBe(DiscoveredRuleNames().Order(StringComparer.Ordinal));

        UncoveredKeys(emitted).ShouldBeEmpty();
    }

    /// <summary>
    /// The named guard of the check above. The check only means something if it
    /// can go red, and nothing in a green run demonstrates that: a comparison
    /// against an allow-list that happens to cover everything looks identical to
    /// a comparison that never compares. This feeds it a rule that emits a key
    /// outside the list and requires the finding.
    /// </summary>
    [Fact]
    public void The_completeness_check_reports_a_rule_that_emits_a_key_outside_the_allow_list()
    {
        var emitted = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            [QuietHoursRule.RuleName] = new HashSet<string>(StringComparer.Ordinal)
            {
                "window",
                "recipientPhoneNumber",
            },
        };

        UncoveredKeys(emitted).ShouldBe([$"{QuietHoursRule.RuleName}.recipientPhoneNumber"]);
    }

    /// <summary>Keys a rule emitted that its allow-list entry does not cover.</summary>
    private static string[] UncoveredKeys(IReadOnlyDictionary<string, IReadOnlySet<string>> emitted)
        => [.. emitted
            .SelectMany(rule => rule.Value
                .Where(key => !PolicyEvidenceProjection.AllowedKeysByRule[rule.Key].Contains(key))
                .Select(key => $"{rule.Key}.{key}"))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public async Task Every_declared_key_is_a_key_some_branch_of_its_rule_really_emits()
    {
        IReadOnlyDictionary<string, IReadOnlySet<string>> emitted = await EmittedKeysByRuleAsync();

        var stale = PolicyEvidenceProjection.AllowedKeysByRule
            .SelectMany(rule => rule.Value
                .Where(key => !emitted[rule.Key].Contains(key))
                .Select(key => $"{rule.Key}.{key}"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        stale.ShouldBeEmpty();
    }

    [Fact]
    public void The_projection_serves_the_allow_listed_members_and_withholds_the_rest_by_name()
    {
        var evidence = JsonSerializer.Serialize(new
        {
            window = "22:00-08:00",
            timezone = "America/Sao_Paulo",
            localTime = "23:30",
            recipientPhoneNumber = "+5511999990000",
        });

        PolicyEvidenceView view = PolicyEvidenceProjection.Project(QuietHoursRule.RuleName, evidence);

        view.Evidence.GetProperty("window").GetString().ShouldBe("22:00-08:00");
        view.Evidence.GetProperty("timezone").GetString().ShouldBe("America/Sao_Paulo");
        view.Evidence.GetProperty("localTime").GetString().ShouldBe("23:30");
        view.Evidence.TryGetProperty("recipientPhoneNumber", out _).ShouldBeFalse();
        view.Evidence.GetRawText().ShouldNotContain("5511999990000");
        view.UndisclosedKeys.ShouldBe(["recipientPhoneNumber"]);
    }

    [Fact]
    public void A_rule_with_no_entry_in_the_allow_list_discloses_nothing_and_declares_every_key()
    {
        var evidence = JsonSerializer.Serialize(new { anything = "value" });

        PolicyEvidenceView view = PolicyEvidenceProjection.Project("RuleNobodyDeclared", evidence);

        view.Evidence.EnumerateObject().ShouldBeEmpty();
        view.UndisclosedKeys.ShouldBe(["anything"]);
    }

    /// <summary>
    /// The rule names the pipeline really ships, discovered from the production
    /// assembly so a new rule joins this check without anyone remembering to add
    /// it here.
    /// </summary>
    private static IEnumerable<string> DiscoveredRuleNames()
        => typeof(ChannelSelectionRule).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IPolicyRule<NotificationContext>).IsAssignableFrom(type))
            .Select(type => (string)type
                .GetField("RuleName", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!
                .GetValue(null)!);

    /// <summary>
    /// Runs each rule across the branches that emit different evidence shapes
    /// and collects the union of the top-level keys they produced.
    /// </summary>
    private static async Task<IReadOnlyDictionary<string, IReadOnlySet<string>>> EmittedKeysByRuleAsync()
    {
        var keys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (PolicyRuleResult result in await ConsentGateBranchesAsync())
        {
            Collect(keys, ConsentGateRule.RuleName, result);
        }

        foreach (PolicyRuleResult result in await SuppressionGateBranchesAsync())
        {
            Collect(keys, SuppressionGateRule.RuleName, result);
        }

        foreach (PolicyRuleResult result in await QuietHoursBranchesAsync())
        {
            Collect(keys, QuietHoursRule.RuleName, result);
        }

        foreach (PolicyRuleResult result in await DedupeWindowBranchesAsync())
        {
            Collect(keys, DedupeWindowRule.RuleName, result);
        }

        foreach (PolicyRuleResult result in await ChannelSelectionBranchesAsync())
        {
            Collect(keys, ChannelSelectionRule.RuleName, result);
        }

        return keys.ToDictionary(
            entry => entry.Key,
            entry => (IReadOnlySet<string>)entry.Value,
            StringComparer.Ordinal);
    }

    private static void Collect(
        Dictionary<string, HashSet<string>> keys,
        string rule,
        PolicyRuleResult result)
    {
        using JsonDocument evidence = JsonDocument.Parse(result.EvidenceJson);
        HashSet<string> forRule = keys.TryGetValue(rule, out HashSet<string>? existing)
            ? existing
            : keys[rule] = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in evidence.RootElement.EnumerateObject())
        {
            forRule.Add(member.Name);
        }
    }

    private static async Task<PolicyRuleResult[]> ConsentGateBranchesAsync()
    {
        var rule = new ConsentGateRule();
        RecipientSnapshot granted = PipelineTestData.Recipient(consents:
            [new ConsentDecision(ConsentPurpose, "sms", true, "app", "v1", DateTimeOffset.UtcNow)]);

        return
        [
            await rule.EvaluateAsync(
                PipelineTestData.Context(recipient: granted),
                PipelineTestData.Policy(),
                CancellationToken.None),
            await rule.EvaluateAsync(
                PipelineTestData.Context(recipient: granted, remainingChannels: ["sms"]),
                PipelineTestData.Policy(consentPurpose: ConsentPurpose),
                CancellationToken.None),
            await rule.EvaluateAsync(
                PipelineTestData.Context(recipient: granted, remainingChannels: ["sms", "email"]),
                PipelineTestData.Policy(consentPurpose: ConsentPurpose),
                CancellationToken.None),
            await rule.EvaluateAsync(
                PipelineTestData.Context(recipient: PipelineTestData.Recipient()),
                PipelineTestData.Policy(consentPurpose: ConsentPurpose),
                CancellationToken.None),
        ];
    }

    private static async Task<PolicyRuleResult[]> SuppressionGateBranchesAsync()
    {
        var rule = new SuppressionGateRule(new FrozenTimeProvider(InsideQuietWindowUtc));
        var smsPoint = Guid.NewGuid();
        var emailPoint = Guid.NewGuid();
        ContactPointSnapshot[] points =
        [
            new(smsPoint, "sms", Verified: true),
            new(emailPoint, "email", Verified: true),
        ];
        SuppressionState[] smsSuppressed =
            [new(smsPoint, "sms", "hard-bounce", InsideQuietWindowUtc, null)];
        SuppressionState[] bothSuppressed =
        [
            new(smsPoint, "sms", "hard-bounce", InsideQuietWindowUtc, null),
            new(emailPoint, "email", "hard-bounce", InsideQuietWindowUtc, null),
        ];

        return
        [
            await rule.EvaluateAsync(
                PipelineTestData.Context(
                    recipient: PipelineTestData.Recipient(contactPoints: points),
                    remainingChannels: ["sms", "email"]),
                PipelineTestData.Policy(),
                CancellationToken.None),
            await rule.EvaluateAsync(
                PipelineTestData.Context(
                    recipient: PipelineTestData.Recipient(
                        contactPoints: points, suppressions: smsSuppressed),
                    remainingChannels: ["sms", "email"]),
                PipelineTestData.Policy(),
                CancellationToken.None),
            await rule.EvaluateAsync(
                PipelineTestData.Context(
                    recipient: PipelineTestData.Recipient(
                        contactPoints: points, suppressions: bothSuppressed),
                    remainingChannels: ["sms", "email"]),
                PipelineTestData.Policy(),
                CancellationToken.None),
        ];
    }

    private static async Task<PolicyRuleResult[]> QuietHoursBranchesAsync()
    {
        var insideWindow = new QuietHoursRule(new FrozenTimeProvider(InsideQuietWindowUtc));

        // 2026-08-23 17:00 UTC is 14:00 in America/Sao_Paulo, outside the window.
        var outsideWindow = new QuietHoursRule(
            new FrozenTimeProvider(new DateTimeOffset(2026, 8, 23, 17, 0, 0, TimeSpan.Zero)));

        return
        [
            await insideWindow.EvaluateAsync(
                PipelineTestData.Context(
                    template: PipelineTestData.Template(), recipient: PipelineTestData.Recipient()),
                PipelineTestData.Policy(quietHours: null),
                CancellationToken.None),
            await insideWindow.EvaluateAsync(
                PipelineTestData.Context(
                    notification: PipelineTestData.AcceptedNotification(NotificationClasses.Critical),
                    template: PipelineTestData.Template(),
                    recipient: PipelineTestData.Recipient()),
                PipelineTestData.Policy(quietHours: QuietWindow),
                CancellationToken.None),
            await insideWindow.EvaluateAsync(
                PipelineTestData.Context(
                    template: PipelineTestData.Template(), recipient: PipelineTestData.Recipient()),
                PipelineTestData.Policy(quietHours: QuietWindow),
                CancellationToken.None),
            await outsideWindow.EvaluateAsync(
                PipelineTestData.Context(
                    template: PipelineTestData.Template(), recipient: PipelineTestData.Recipient()),
                PipelineTestData.Policy(quietHours: QuietWindow),
                CancellationToken.None),
        ];
    }

    private static async Task<PolicyRuleResult[]> DedupeWindowBranchesAsync()
    {
        DedupeBarrierOutcome[] outcomes =
        [
            DedupeBarrierOutcome.Acquired,
            DedupeBarrierOutcome.AlreadyHeld,
            DedupeBarrierOutcome.Duplicate,
            DedupeBarrierOutcome.Unavailable,
        ];

        var results = new List<PolicyRuleResult>(outcomes.Length);
        foreach (DedupeBarrierOutcome outcome in outcomes)
        {
            IDedupeBarrier barrier = Substitute.For<IDedupeBarrier>();
            barrier.TryAcquireAsync(
                    Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                    Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
                .Returns(outcome);
            results.Add(await new DedupeWindowRule(barrier).EvaluateAsync(
                PipelineTestData.Context(), PipelineTestData.Policy(), CancellationToken.None));
        }

        return [.. results];
    }

    private static async Task<PolicyRuleResult[]> ChannelSelectionBranchesAsync()
    {
        var rule = new ChannelSelectionRule();
        return
        [
            await rule.EvaluateAsync(
                PipelineTestData.Context(
                    template: PipelineTestData.Template(),
                    recipient: PipelineTestData.Recipient()),
                PipelineTestData.Policy(),
                CancellationToken.None),
            await rule.EvaluateAsync(
                PipelineTestData.Context(
                    template: PipelineTestData.Template(),
                    recipient: PipelineTestData.Recipient(contactPoints: [])),
                PipelineTestData.Policy(),
                CancellationToken.None),
        ];
    }
}
