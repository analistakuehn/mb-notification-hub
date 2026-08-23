using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Features.Pipeline.Rules;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;

/// <summary>
/// Third stage: loads the published class policy and runs the ordered rule
/// list over it. Each rule reads its slice of the definition, receives the
/// remaining channel set, and records its decision as a policy_evaluation
/// row: the policy decision is auditable rule by rule. Filters intersect;
/// the first defer or reject stops the run. A missing published policy is an
/// operational misconfiguration, never a business rejection: the exception
/// propagates and the message returns to the queue.
/// </summary>
internal sealed class PolicyStage(
    IPublishedCatalog catalog,
    IReadOnlyList<IPolicyRule<NotificationContext>> rules,
    TimeProvider timeProvider) : INotificationStage
{
    public string Name => "Policy";

    public async Task<StageOutcome> ExecuteAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        Result<PublishedClassPolicy> policy = await catalog.FindClassPolicyAsync(
            context.Notification.Application, context.Notification.Class, cancellationToken);
        if (policy.IsFailure)
        {
            throw new InvalidOperationException(
                $"A aplicação '{context.Notification.Application}' não tem política publicada para a classe "
                + $"'{context.Notification.Class}'; o pipeline não decide sem política. Detalhe: {policy.Error}");
        }

        context.Policy = policy.Value;
        context.InitializeRemainingChannels(
            policy.Value!.Definition.ChannelsAllowed.Select(channel => channel.Value));

        foreach (IPolicyRule<NotificationContext> rule in rules)
        {
            PolicyRuleResult result = await rule.EvaluateAsync(
                context, policy.Value!.Definition, cancellationToken);
            Record(context, rule.Name, result);
            switch (result)
            {
                case PolicyRuleResult.Allow:
                    break;
                case PolicyRuleResult.FilterChannels filter:
                    context.RestrictRemainingChannels(filter.Channels.Select(channel => channel.Value));
                    if (context.RemainingChannels.Count == 0)
                    {
                        throw new InvalidOperationException(
                            $"A regra '{rule.Name}' filtrou todos os canais sem rejeitar; "
                            + "uma regra que esvazia o conjunto deve devolver Reject com motivo canônico.");
                    }

                    break;
                case PolicyRuleResult.Defer defer:
                    context.DeferReleaseAt = defer.ReleaseAt;
                    context.LastReason = QuietHoursRuleReason(rule.Name);
                    return StageOutcome.Defer;
                case PolicyRuleResult.Reject reject:
                    context.LastReason = reject.Reason;
                    return StageOutcome.Reject;
                default:
                    throw new InvalidOperationException(
                        $"Resultado de regra não suportado: {result.GetType().Name}.");
            }
        }

        return StageOutcome.Continue;
    }

    private void Record(NotificationContext context, string ruleName, PolicyRuleResult result)
    {
        (var resultValue, var reason) = result switch
        {
            PolicyRuleResult.Allow => (PolicyEvaluationResults.Allow, (string?)null),
            PolicyRuleResult.FilterChannels => (PolicyEvaluationResults.FilterChannels, null),
            PolicyRuleResult.Defer => (PolicyEvaluationResults.Defer, QuietHoursRuleReason(ruleName)),
            PolicyRuleResult.Reject reject => (PolicyEvaluationResults.Reject, reject.Reason),
            _ => throw new InvalidOperationException($"Resultado de regra não suportado: {result.GetType().Name}."),
        };
        context.PolicyEvaluations.Add(PolicyEvaluation.Record(
            context.Notification.Id,
            ruleName,
            resultValue,
            reason,
            result.EvidenceJson,
            timeProvider.GetUtcNow()));
    }

    /// <summary>The stable deferral reason; today only the quiet-hours rule defers.</summary>
    private static string QuietHoursRuleReason(string ruleName)
        => ruleName == QuietHoursRule.RuleName
            ? QuietHoursRule.ReasonQuietHours
            : "deferred";
}
