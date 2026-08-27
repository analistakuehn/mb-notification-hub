using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;

/// <summary>
/// Fifth stage: turns the surviving delivery plan into the first attempt's
/// routing. The first step names the channel and the fallback wait; the
/// target contact point comes from the snapshot (verified values first),
/// except for push, whose targets are device tokens the dispatcher fans out.
/// The destination queue derives from channel and class, with the auth
/// suffix when the template's purpose is authentication.
/// </summary>
internal sealed class RouteStage : INotificationStage
{
    private const string PushChannel = "push";

    public string Name => "Route";

    public Task<StageOutcome> ExecuteAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        DeliveryPlanStep firstStep = context.DeliveryPlan is { Count: > 0 } plan
            ? plan[0]
            : throw new InvalidOperationException("O estágio Route requer o plano de entrega filtrado.");
        RecipientSnapshot recipient = context.Recipient
            ?? throw new InvalidOperationException("O estágio Route requer o destinatário resolvido.");

        var channel = firstStep.Channel.Value;
        context.SelectedContactPointId = channel == PushChannel
            ? null
            : recipient.ContactPoints
                .Where(point => string.Equals(point.Channel, channel, StringComparison.Ordinal))
                .OrderByDescending(point => point.Verified)
                .ThenBy(point => point.ContactPointId)
                .Select(point => (Guid?)point.ContactPointId)
                .FirstOrDefault();
        context.FallbackTimeout = firstStep.Timeout;
        context.DispatchDestination = DestinationFor(
            context.Template?.Purpose, channel, context.Notification.Class);
        return Task.FromResult(StageOutcome.Continue);
    }

    /// <summary>
    /// The dispatch queue of one attempt: the auth suffix when the template's
    /// purpose is authentication, otherwise the notification's class. Shared
    /// with the fallback path, so both producers route identically.
    /// </summary>
    internal static string DestinationFor(string? templatePurpose, string channel, string notificationClass)
        => TemplatePurposes.IsAuthentication(templatePurpose)
            ? $"dispatch-{channel}-auth"
            : $"dispatch-{channel}-{notificationClass}";
}
