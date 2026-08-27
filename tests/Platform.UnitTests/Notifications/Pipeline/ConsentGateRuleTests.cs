using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.UnitTests.Notifications.Pipeline;

public sealed class ConsentGateRuleTests
{
    private static ConsentDecision Consent(string purpose, string channel, bool granted)
        => new(purpose, channel, granted, "app", "v1", DateTimeOffset.UtcNow);

    [Fact]
    public async Task A_null_purpose_allows_with_contractual_basis_evidence()
    {
        NotificationContext context = PipelineTestData.Context(recipient: PipelineTestData.Recipient());

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: null), CancellationToken.None);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        using var evidence = JsonDocument.Parse(allow.EvidenceJson);
        evidence.RootElement.GetProperty("basis").GetString().ShouldBe("contractual-or-legal");
    }

    [Fact]
    public async Task No_granted_channel_rejects_with_the_canonical_no_consent_reason()
    {
        NotificationContext context = PipelineTestData.Context(
            recipient: PipelineTestData.Recipient(consents:
                [Consent("marketing", "sms", granted: false)]));

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: "marketing"), CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Reject>().Reason.ShouldBe("no-consent");
    }

    [Fact]
    public async Task Partially_granted_channels_filter_to_the_granted_subset()
    {
        NotificationContext context = PipelineTestData.Context(
            recipient: PipelineTestData.Recipient(consents:
            [
                Consent("marketing", "sms", granted: true),
                Consent("marketing", "email", granted: false),
            ]));

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: "marketing"), CancellationToken.None);

        PolicyRuleResult.FilterChannels filter = result.ShouldBeOfType<PolicyRuleResult.FilterChannels>();
        filter.Channels.Select(channel => channel.Value).ShouldBe(["sms"]);
    }

    [Fact]
    public async Task Every_channel_granted_allows_without_filtering()
    {
        NotificationContext context = PipelineTestData.Context(
            remainingChannels: ["sms", "email"],
            recipient: PipelineTestData.Recipient(consents:
            [
                Consent("marketing", "sms", granted: true),
                Consent("marketing", "email", granted: true),
            ]));

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: "marketing"), CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Allow>();
    }

    [Fact]
    public async Task A_revoked_consent_does_not_grant_even_with_an_older_grant_present()
    {
        // The snapshot carries only the state in force per (purpose, channel);
        // a revocation arrives as granted=false and must deny the channel.
        NotificationContext context = PipelineTestData.Context(
            remainingChannels: ["sms"],
            recipient: PipelineTestData.Recipient(consents:
                [Consent("marketing", "sms", granted: false)]));

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: "marketing"), CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Reject>().Reason.ShouldBe("no-consent");
    }

    [Theory]
    [InlineData("Marketing")]
    [InlineData("MARKETING")]
    [InlineData(" marketing ")]
    public async Task A_policy_purpose_spelled_otherwise_still_reads_the_stance_it_names(string declared)
    {
        // The class policy authors its purpose in another module, so the rule
        // canonicalizes it before comparing it with the snapshot key.
        NotificationContext context = PipelineTestData.Context(
            remainingChannels: ["sms"],
            recipient: PipelineTestData.Recipient(consents:
                [Consent("marketing", "sms", granted: true)]));

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: declared), CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Allow>();
    }

    [Fact]
    public async Task A_revocation_denies_a_policy_that_names_the_purpose_in_another_case()
    {
        NotificationContext context = PipelineTestData.Context(
            remainingChannels: ["sms"],
            recipient: PipelineTestData.Recipient(consents:
                [Consent("marketing", "sms", granted: false)]));

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: "Marketing"), CancellationToken.None);

        result.ShouldBeOfType<PolicyRuleResult.Reject>().Reason.ShouldBe("no-consent");
    }

    [Fact]
    public async Task The_evidence_records_the_key_that_was_compared()
    {
        NotificationContext context = PipelineTestData.Context(
            remainingChannels: ["sms"],
            recipient: PipelineTestData.Recipient(consents:
                [Consent("marketing", "sms", granted: true)]));

        PolicyRuleResult result = await new ConsentGateRule().EvaluateAsync(
            context, PipelineTestData.Policy(consentPurpose: " Marketing "), CancellationToken.None);

        PolicyRuleResult.Allow allow = result.ShouldBeOfType<PolicyRuleResult.Allow>();
        using var evidence = JsonDocument.Parse(allow.EvidenceJson);
        evidence.RootElement.GetProperty("purpose").GetString().ShouldBe("marketing");
    }
}
