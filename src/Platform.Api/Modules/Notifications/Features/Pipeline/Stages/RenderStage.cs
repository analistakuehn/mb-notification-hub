using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Privacy;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;

/// <summary>
/// Fourth stage: renders the published version for the channel of the first
/// surviving plan step, with the layout the version pins and the sensitive
/// variables masked on the trail form. The full-content hash is computed
/// before any masking and the masked hash over what a trail may store; the
/// stored content is the envelope-encrypted render, sealed with the
/// application's data key, exactly like the ingestion seals the variables,
/// carrying both forms until the send reaches a verdict.
/// </summary>
internal sealed class RenderStage(
    IPublishedTemplateRenderer renderer,
    IEnvelopeCipher cipher) : INotificationStage
{
    internal const string ReasonRenderFailed = "template-render-failed";

    /// <summary>
    /// Refusal the published renderer answers with when the SMS render of an
    /// authentication template produces a link. The word travels as the whole
    /// error text of the failed render, and this stage recognizes it to keep
    /// the producer's diagnosis: collapsing it into a render failure would say
    /// the template is broken when what happened is that a security rule
    /// refused the content.
    /// </summary>
    internal const string ReasonAuthenticationSmsLink =
        NotificationRejectionReasons.AuthenticationSmsLink;

    /// <summary>
    /// Refusal the published renderer answers with when the layout the version
    /// pins is disabled. It travels as the whole error text of the failed
    /// render, exactly like the security refusal above, and for the same
    /// reason: the producer has to tell a template it must fix from a wrapper
    /// somebody else took out of service.
    /// </summary>
    internal const string ReasonLayoutDisabled = NotificationRejectionReasons.LayoutDisabled;

    /// <summary>
    /// Refusal the published renderer answers with when the render is larger
    /// than the channel carries. It travels as the whole error text of the
    /// failed render, exactly like the two refusals above, and for the same
    /// reason: a producer has to tell a template it cannot fix from a variable
    /// value it can shorten.
    /// </summary>
    internal const string ReasonRenderedContentTooLarge =
        NotificationRejectionReasons.RenderedContentTooLarge;

    internal const string FallbackLocale = "pt-BR";

    public string Name => "Render";

    public async Task<StageOutcome> ExecuteAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        DeliveryPlanStep firstStep = context.DeliveryPlan is { Count: > 0 } plan
            ? plan[0]
            : throw new InvalidOperationException("O estágio Render requer o plano de entrega filtrado.");
        PublishedTemplate template = context.Template
            ?? throw new InvalidOperationException("O estágio Render requer o template resolvido.");

        var locale = context.Recipient?.Locale ?? template.DefaultLocale ?? FallbackLocale;
        Result<PublishedTemplateRender> render = await renderer.RenderAsync(
            new PublishedRenderRequest
            {
                Application = context.Notification.Application,
                TemplateKey = context.Notification.TemplateKey,
                Channel = firstStep.Channel.Value,
                Locale = locale,
                Variables = context.Variables,
                IncludeMaskedForm = true,
            },
            cancellationToken);
        if (render.IsFailure)
        {
            // A render failure with validated variables is a governance drift
            // (content changed between ingestion and processing): a business
            // rejection with a stable reason, auditable, never a retry loop.
            // The refusals the renderer words for itself keep their own
            // reasons, because each asks something different of whoever reads
            // the rejection.
            context.LastReason = ReasonForFailedRender(render.Error);
            return StageOutcome.Reject;
        }

        context.Render = render.Value;
        context.RenderedContentEncrypted = await RenderedContentEnvelope.SealAsync(
            cipher, context.Notification.Application, render.Value!, cancellationToken);
        return StageOutcome.Continue;
    }

    /// <summary>
    /// Which rejection a failed render is. The renderer words three refusals of
    /// its own and answers with the bare word, so recognizing them is an
    /// equality against that word; everything else is a template to fix. One
    /// table answers for the ingestion path and for the fallback path alike:
    /// two copies would drift, and the drift reads as a producer whose
    /// notification was refused for one reason on one path and another reason
    /// on the other.
    /// </summary>
    internal static string ReasonForFailedRender(string? error) => error switch
    {
        ReasonAuthenticationSmsLink => ReasonAuthenticationSmsLink,
        ReasonLayoutDisabled => ReasonLayoutDisabled,
        ReasonRenderedContentTooLarge => ReasonRenderedContentTooLarge,
        _ => ReasonRenderFailed,
    };
}
