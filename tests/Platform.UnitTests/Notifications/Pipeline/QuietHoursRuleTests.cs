using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
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

    /// <summary>
    /// A declared timezone the runtime cannot resolve used to throw out of the
    /// stage whose whole contract is to produce a decision, so every
    /// notification of that recipient went to retry and then to the dead letter
    /// queue. The rule now reads it the way the recipient contract already
    /// reads a recipient who declared nothing, and says so in the evidence.
    /// </summary>
    [Fact]
    public async Task An_unresolvable_timezone_falls_back_to_the_platform_default_and_says_so()
    {
        NotificationContext context = PipelineTestData.Context(
            template: PipelineTestData.Template(),
            recipient: PipelineTestData.Recipient(timezone: "America/Nao_Existe"));
        ClassPolicyDefinition policy = PipelineTestData.Policy(
            quietHours: new QuietHoursWindow(new TimeOnly(22, 0), new TimeOnly(8, 0)));

        PolicyRuleResult result = await Rule(InsideWindowUtc).EvaluateAsync(
            context, policy, CancellationToken.None);

        // The same deferral the platform default produces, reached instead of
        // an exception, with both identifiers in the trail so a window measured
        // in a timezone the recipient never declared is visible as such.
        PolicyRuleResult.Defer defer = result.ShouldBeOfType<PolicyRuleResult.Defer>();
        defer.ReleaseAt.ShouldBe(new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.Zero));
        defer.EvidenceJson.ShouldNotBeNull();
        defer.EvidenceJson.Contains(RecipientSnapshot.DefaultTimezone, StringComparison.Ordinal)
            .ShouldBeTrue();
        defer.EvidenceJson.Contains("America/Nao_Existe", StringComparison.Ordinal).ShouldBeTrue(
            "a evidência tem de nomear o valor declarado, senão a substituição fica "
            + "indistinguível de uma janela medida no fuso que o destinatário pediu.");
    }

    [Fact]
    public void A_resolvable_timezone_is_used_exactly_as_declared()
        => QuietHoursRule.ResolveTimezone("America/Sao_Paulo").Resolved.ShouldBe("America/Sao_Paulo");

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
