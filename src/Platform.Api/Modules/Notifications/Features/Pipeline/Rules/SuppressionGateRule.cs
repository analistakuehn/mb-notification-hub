using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;

/// <summary>
/// Second policy rule: the destinations the hub stopped addressing. A provider
/// that refused a destination definitively already told this hub the message
/// will not arrive, and sending anyway spends deliverability reputation on a
/// message nobody reads.
/// <para>
/// It sits after the consent gate and before the silence window on purpose.
/// After consent, because a recipient who never allowed the channel is refused
/// for a stronger reason and the trail should say so. Before the window,
/// because deferring a notification for hours to reject it in the morning is
/// work nobody asked for.
/// </para>
/// <para>
/// A channel falls only when every address the recipient still has on it is
/// suppressed. A recipient who keeps a second address on the same channel is
/// reachable there, and taking the channel away over the dead one would turn a
/// protection for the recipient into a delivery failure against them.
/// </para>
/// </summary>
internal sealed class SuppressionGateRule(TimeProvider timeProvider) : IPolicyRule<NotificationContext>
{
    internal const string RuleName = "SuppressionGate";
    internal const string ReasonChannelSuppressed = NotificationRejectionReasons.ChannelSuppressed;

    public string Name => RuleName;

    public Task<PolicyRuleResult> EvaluateAsync(
        NotificationContext context,
        ClassPolicyDefinition policy,
        CancellationToken cancellationToken)
    {
        RecipientSnapshot recipient = context.Recipient
            ?? throw new InvalidOperationException("A regra de supressão requer o destinatário resolvido.");

        DateTimeOffset now = timeProvider.GetUtcNow();
        var suppressedPoints = recipient.Suppressions
            .Where(suppression => suppression.Until is null || suppression.Until > now)
            .Select(suppression => suppression.ContactPointId)
            .ToHashSet();

        var suppressed = new List<string>();
        var surviving = new List<string>();
        foreach (var channel in context.RemainingChannels.Order(StringComparer.Ordinal))
        {
            List<ContactPointSnapshot> points = [.. recipient.ContactPoints
                .Where(point => string.Equals(point.Channel, channel, StringComparison.Ordinal))];
            var blocked = points.Count > 0
                && points.TrueForAll(point => suppressedPoints.Contains(point.ContactPointId));
            (blocked ? suppressed : surviving).Add(channel);
        }

        var evidence = JsonSerializer.Serialize(new
        {
            remaining = context.RemainingChannels.Order(StringComparer.Ordinal),
            suppressed,
            surviving,
        });

        if (suppressed.Count == 0)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Allow { EvidenceJson = evidence });
        }

        if (surviving.Count == 0)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Reject(ReasonChannelSuppressed)
            {
                EvidenceJson = evidence,
            });
        }

        return Task.FromResult<PolicyRuleResult>(
            new PolicyRuleResult.FilterChannels(ToChannelSet(surviving)) { EvidenceJson = evidence });
    }

    private static HashSet<Channel> ToChannelSet(IEnumerable<string> values)
        => values.Select(value => Channel.Create(value).Value!).ToHashSet();
}
