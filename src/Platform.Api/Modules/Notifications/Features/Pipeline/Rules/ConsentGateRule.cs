using System.Text.Json;
using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;

/// <summary>
/// First policy rule: consent by purpose. A null consent purpose means the
/// class operates on a contractual or legal basis and consults nothing; the
/// evidence records that basis explicitly. With a purpose, only channels
/// whose latest ledger decision grants it survive; no surviving channel
/// rejects the notification with the canonical no-consent reason.
/// </summary>
internal sealed class ConsentGateRule : IPolicyRule<NotificationContext>
{
    internal const string RuleName = "ConsentGate";
    internal const string ReasonNoConsent = "no-consent";

    public string Name => RuleName;

    public Task<PolicyRuleResult> EvaluateAsync(
        NotificationContext context,
        ClassPolicyDefinition policy,
        CancellationToken cancellationToken)
    {
        if (policy.ConsentPurpose is not { Length: > 0 } purpose)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Allow
            {
                EvidenceJson = JsonSerializer.Serialize(new { basis = "contractual-or-legal", purpose = (string?)null }),
            });
        }

        RecipientSnapshot recipient = context.Recipient
            ?? throw new InvalidOperationException("A regra de consentimento requer o destinatário resolvido.");
        var granted = new List<string>();
        var denied = new List<string>();
        foreach (var channel in context.RemainingChannels.Order(StringComparer.Ordinal))
        {
            var hasConsent = recipient.Consents.Any(consent =>
                consent.Granted
                && string.Equals(consent.Purpose, purpose, StringComparison.Ordinal)
                && string.Equals(consent.Channel, channel, StringComparison.Ordinal));
            (hasConsent ? granted : denied).Add(channel);
        }

        var evidence = JsonSerializer.Serialize(new { purpose, granted, denied });
        if (granted.Count == 0)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Reject(ReasonNoConsent)
            {
                EvidenceJson = evidence,
            });
        }

        if (denied.Count == 0)
        {
            return Task.FromResult<PolicyRuleResult>(new PolicyRuleResult.Allow { EvidenceJson = evidence });
        }

        return Task.FromResult<PolicyRuleResult>(
            new PolicyRuleResult.FilterChannels(ToChannelSet(granted)) { EvidenceJson = evidence });
    }

    private static HashSet<Channel> ToChannelSet(IEnumerable<string> values)
        => values.Select(value => Channel.Create(value).Value!).ToHashSet();
}
