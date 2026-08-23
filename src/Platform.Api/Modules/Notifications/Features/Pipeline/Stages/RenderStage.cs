using System.Text.Json;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;

/// <summary>
/// Fourth stage: renders the published version for the channel of the first
/// surviving plan step, with the layout the version pins and the sensitive
/// variables masked on the trail form. The full-content hash is computed
/// before any masking and the masked hash over what a trail may store; the
/// stored content is the envelope-encrypted full form, sealed with the
/// application's data key, exactly like the ingestion seals the variables.
/// </summary>
internal sealed class RenderStage(
    IPublishedTemplateRenderer renderer,
    IEnvelopeCipher cipher) : INotificationStage
{
    internal const string ReasonRenderFailed = "template-render-failed";
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
            context.LastReason = ReasonRenderFailed;
            return StageOutcome.Reject;
        }

        context.Render = render.Value;
        context.RenderedContentEncrypted = await EncryptRenderedContentAsync(
            context.Notification.Application, render.Value!, cancellationToken);
        return StageOutcome.Continue;
    }

    private async Task<byte[]> EncryptRenderedContentAsync(
        string application,
        PublishedTemplateRender render,
        CancellationToken cancellationToken)
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            channel = render.Channel,
            locale = render.ResolvedLocale,
            subject = render.Full.Subject,
            body = render.Full.Body,
            bodyText = render.Full.BodyText,
        });
        return await cipher.EncryptAsync(application, plaintext, cancellationToken);
    }
}
