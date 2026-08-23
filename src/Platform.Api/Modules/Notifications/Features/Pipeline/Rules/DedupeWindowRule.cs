using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Redis;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;

/// <summary>
/// Third policy rule: the atomic duplicate barrier over
/// (application, templateKey, recipientId) inside the policy's dedupe
/// window. The barrier value carries the notification id, so a redelivery of
/// the same notification recognizes its own mark instead of rejecting
/// itself. An unreachable Redis fails open: a possible duplicate is the
/// accepted risk, and the evidence records the fail-open explicitly.
/// </summary>
internal sealed class DedupeWindowRule(IDedupeBarrier barrier) : IPolicyRule<NotificationContext>
{
    internal const string RuleName = "DedupeWindow";
    internal const string ReasonDuplicateWindow = "duplicate-window";

    public string Name => RuleName;

    public async Task<PolicyRuleResult> EvaluateAsync(
        NotificationContext context,
        ClassPolicyDefinition policy,
        CancellationToken cancellationToken)
    {
        var windowSeconds = (int)policy.DedupeWindow.TotalSeconds;
        DedupeBarrierOutcome outcome = await barrier.TryAcquireAsync(
            context.Notification.Application,
            context.Notification.TemplateKey,
            context.Notification.RecipientId,
            context.Notification.Id,
            policy.DedupeWindow,
            cancellationToken);
        return outcome switch
        {
            DedupeBarrierOutcome.Acquired => new PolicyRuleResult.Allow
            {
                EvidenceJson = JsonSerializer.Serialize(new { windowSeconds, acquired = true }),
            },
            DedupeBarrierOutcome.AlreadyHeld => new PolicyRuleResult.Allow
            {
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    windowSeconds,
                    acquired = false,
                    heldByThisNotification = true,
                }),
            },
            DedupeBarrierOutcome.Duplicate => new PolicyRuleResult.Reject(ReasonDuplicateWindow)
            {
                EvidenceJson = JsonSerializer.Serialize(new { windowSeconds, acquired = false }),
            },
            DedupeBarrierOutcome.Unavailable => new PolicyRuleResult.Allow
            {
                EvidenceJson = JsonSerializer.Serialize(new
                {
                    windowSeconds,
                    failOpen = true,
                    risk = "duplicate-possible",
                }),
            },
            _ => throw new InvalidOperationException($"Resultado de barreira desconhecido: {outcome}."),
        };
    }
}
