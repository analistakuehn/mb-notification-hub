using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;

/// <summary>
/// Last policy rule: intersects the surviving channels with the delivery
/// plan, the channels the published version ships content for, and the
/// channels the recipient can actually receive on (an active contact point,
/// or an active device token for push). The surviving plan keeps the
/// published order; no surviving channel rejects with the canonical
/// no-valid-contact reason. The producer's channel hint is not persisted at
/// ingestion in this phase, so no hint reordering applies here.
/// </summary>
internal sealed class ChannelSelectionRule : IPolicyRule<NotificationContext>
{
    internal const string RuleName = "ChannelSelection";
    internal const string ReasonNoValidContact = ResolveStage.ReasonNoValidContact;

    private const string PushChannel = "push";

    public string Name => RuleName;

    public Task<PolicyRuleResult> EvaluateAsync(
        NotificationContext context,
        ClassPolicyDefinition policy,
        CancellationToken cancellationToken)
    {
        RecipientSnapshot recipient = context.Recipient
            ?? throw new InvalidOperationException("A seleção de canal requer o destinatário resolvido.");
        PublishedTemplate template = context.Template
            ?? throw new InvalidOperationException("A seleção de canal requer o template resolvido.");

        var contentChannels = template.ChannelsWithContent
            .Select(channel => channel.Value)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> reachableChannels = ReachableChannels(recipient);
        List<DeliveryPlanStep> survivingPlan = [.. policy.DeliveryPlan
            .Where(step => context.RemainingChannels.Contains(step.Channel.Value)
                && contentChannels.Contains(step.Channel.Value)
                && reachableChannels.Contains(step.Channel.Value))];

        var evidence = JsonSerializer.Serialize(new
        {
            remaining = context.RemainingChannels.Order(StringComparer.Ordinal),
            plan = policy.DeliveryPlan.Select(step => step.Channel.Value),
            withContent = contentChannels.Order(StringComparer.Ordinal),
            reachable = reachableChannels.Order(StringComparer.Ordinal),
            selected = survivingPlan.Select(step => step.Channel.Value),
        });

        if (survivingPlan.Count == 0)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Reject(ReasonNoValidContact)
            {
                EvidenceJson = evidence,
            });
        }

        context.DeliveryPlan = survivingPlan;
        return Task.FromResult<PolicyRuleResult>(
            new PolicyRuleResult.FilterChannels(survivingPlan.Select(step => step.Channel).ToHashSet())
            {
                EvidenceJson = evidence,
            });
    }

    private static HashSet<string> ReachableChannels(RecipientSnapshot recipient)
    {
        var reachable = recipient.ContactPoints
            .Select(point => point.Channel)
            .ToHashSet(StringComparer.Ordinal);
        if (recipient.Devices.Count > 0)
        {
            reachable.Add(PushChannel);
        }

        return reachable;
    }
}
