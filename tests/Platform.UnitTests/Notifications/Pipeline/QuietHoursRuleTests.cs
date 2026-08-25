using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class QuietHoursRuleTests
{
    // 2026-08-23 02:30 UTC = 2026-08-22 23:30 in America/Sao_Paulo (UTC-3).
    private static readonly DateTimeOffset InsideWindowUtc = new(2026, 8, 23, 2, 30, 0, TimeSpan.Zero);

    private static QuietHoursRule Rule(DateTimeOffset nowUtc)
        => new(new FrozenTimeProvider(nowUtc));

    [Fact]
    public async Task A_null_window_allows_with_evidence()
    {
        NotificationContext context = PipelineTestData.Context(recipient: PipelineTestData.Recipient());

        PolicyRuleResult result = await Rule(InsideWindowUtc).EvaluateAsync(
            context, PipelineTestData.Policy(quietHours: null), CancellationToken.None);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        using var evidence = JsonDocument.Parse(allow.EvidenceJson);
        evidence.RootElement.GetProperty("window").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Inside_the_window_a_transactional_notification_defers_to_the_window_end()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(),
            recipient: PipelineTestData.Recipient());
        ClassPolicyDefinition policy = PipelineTestData.Policy(
            quietHours: new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(8, 0)));

        PolicyRuleResult result = await Rule(InsideWindowUtc).EvaluateAsync(
            context, policy, CancellationToken.None);

        PolicyRuleResult.Defer defer = result.ShouldBeOfType<PolicyRuleResult.Defer>();
        // 23:30 local sits in the 22:00-08:00 window; release is 08:00 local
        // of the next day, 2026-08-23 11:00 UTC.
        defer.ReleaseAt.ShouldBe(new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Inside_the_early_morning_side_of_a_wrapped_window_the_release_is_the_same_day()
    {
        // 2026-08-23 09:00 UTC = 06:00 local, inside 22:00-08:00.
        var earlyMorningUtc = new DateTimeOffset(2026, 8, 23, 9, 0, 0, TimeSpan.Zero);
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(),
            recipient: PipelineTestData.Recipient());
        ClassPolicyDefinition policy = PipelineTestData.Policy(
            quietHours: new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(8, 0)));

        PolicyRuleResult result = await Rule(earlyMorningUtc).EvaluateAsync(
            context, policy, CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Defer>()
            .ReleaseAt.ShouldBe(new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task Outside_the_window_the_notification_proceeds()
    {
        // 2026-08-23 17:00 UTC = 14:00 local, outside 22:00-08:00.
        var afternoonUtc = new DateTimeOffset(2026, 8, 23, 17, 0, 0, TimeSpan.Zero);
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(),
            recipient: PipelineTestData.Recipient());
        ClassPolicyDefinition policy = PipelineTestData.Policy(
            quietHours: new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(8, 0)));

        PolicyRuleResult result = await Rule(afternoonUtc).EvaluateAsync(
            context, policy, CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Allow>();
    }

    [Fact]
    public async Task The_guard_never_defers_a_critical_notification_even_inside_the_window()
    {
        NotificationContext context = PipelineTestData.Context(
            notification: PipelineTestData.AcceptedNotification(NotificationClasses.Critical),
            template: PipelineTestData.Template(),
            recipient: PipelineTestData.Recipient());
        ClassPolicyDefinition policy = PipelineTestData.Policy(
            quietHours: new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(8, 0)));

        PolicyRuleResult result = await Rule(InsideWindowUtc).EvaluateAsync(
            context, policy, CancellationToken.None);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        allow.EvidenceJson.ShouldContain("critical-or-authentication-never-deferred");
    }

    [Fact]
    public async Task The_guard_never_defers_an_authentication_template_even_inside_the_window()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(purpose: "authentication"),
            recipient: PipelineTestData.Recipient());
        ClassPolicyDefinition policy = PipelineTestData.Policy(
            quietHours: new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(8, 0)));

        PolicyRuleResult result = await Rule(InsideWindowUtc).EvaluateAsync(
            context, policy, CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Allow>()
            .EvidenceJson.ShouldContain("critical-or-authentication-never-deferred");
    }
}
