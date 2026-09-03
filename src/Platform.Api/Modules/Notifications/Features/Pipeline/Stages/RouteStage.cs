using NotificationHub.Api.Modules.ContactConsent.Integration.V1;
using NotificationHub.Api.Modules.Dispatch.Integration.V1;
using NotificationHub.Api.Modules.Notifications.Domain;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;

/// <summary>
/// Fifth stage: turns the surviving delivery plan into the first attempt's
/// routing. The first step names the channel and the fallback wait; the
/// target contact point comes from the snapshot (verified values first),
/// except for push, whose targets are device tokens the dispatcher fans out.
/// The destination queue derives from channel and class, with the auth
/// suffix when the template's purpose is authentication.
/// <para>
/// It is also where a notification carrying an accepted set of attachments
/// meets the one question the plan cannot answer for itself: whether the
/// channel it takes composes that set into the call it makes. A channel that
/// does not ends the notification here, with a published reason.
/// </para>
/// </summary>
internal sealed class RouteStage(IChannelAttachmentSupport attachmentSupport) : INotificationStage
{
    /// <summary>Derived from the canonical vocabulary, never re-spelled here.</summary>
    private static readonly string PushChannel = Channel.Push.Value;

    /// <summary>
    /// The channel the plan takes does not carry the set this notification was
    /// accepted over. It travels as the reason of the rejection, from the
    /// closed catalog, and it is the same word the fallback ends on and the
    /// same word the send refuses with: one condition, told once.
    /// </summary>
    internal const string ReasonAttachmentsNotCarried =
        NotificationRejectionReasons.AttachmentsNotCarriedByChannel;

    public string Name => "Route";

    public async Task<StageOutcome> ExecuteAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        DeliveryPlanStep firstStep = context.DeliveryPlan is { Count: > 0 } plan
            ? plan[0]
            : throw new InvalidOperationException("O estágio Route requer o plano de entrega filtrado.");
        RecipientSnapshot recipient = context.Recipient
            ?? throw new InvalidOperationException("O estágio Route requer o destinatário resolvido.");

        // Asked before anything is routed, and only of a notification that
        // carries a set. The plan is read and never rewritten: a set that the
        // first step cannot carry ends the notification instead of promoting
        // the step behind it, because which channel this notification takes was
        // decided by policy and eligibility, and letting an attachment reorder
        // it would deliver by a channel nobody planned for.
        if (CarriesASet(context.Notification)
            && !await ChannelCarriesTheSetAsync(firstStep.Channel, cancellationToken))
        {
            context.LastReason = ReasonAttachmentsNotCarried;
            return StageOutcome.Reject;
        }

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
        return StageOutcome.Continue;
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

    /// <summary>
    /// Whether this notification was accepted over a set at all, read off its
    /// own row and out of nowhere else.
    /// <para>
    /// A document that no longer reads is deliberately not this stage's
    /// question. It is an operational defect over the whole notification rather
    /// than a fact about the plan, and the path that could still reach a
    /// provider refuses it before it claims anything, so nothing leaves over a
    /// set nobody can name whichever answer is given here.
    /// </para>
    /// </summary>
    private static bool CarriesASet(Notification notification)
        => AcceptedAttachmentManifest.Read(notification.AcceptedAttachmentsJson)
            is AcceptedManifestRead.Present;

    /// <summary>
    /// Whether the adapter this deployment would call for the channel composes
    /// the set into its call.
    /// <para>
    /// A resolution failure is a deployment defect and is thrown, exactly as
    /// the send throws on the same failure: the message returns with backoff
    /// and heals when the configuration does. Turning it into a rejection
    /// would tell a producer its notification was refused for a reason that is
    /// ours, and it would end a notification that is still deliverable. Only a
    /// notification carrying a set ever reaches this, so a deployment that
    /// sends none is never held by it.
    /// </para>
    /// </summary>
    private async Task<bool> ChannelCarriesTheSetAsync(
        Channel channel,
        CancellationToken cancellationToken)
    {
        Result<bool> carries = await attachmentSupport.CarriesAttachmentsAsync(
            channel, cancellationToken);
        return carries.IsFailure
            ? throw new InvalidOperationException(carries.Error)
            : carries.Value;
    }
}
