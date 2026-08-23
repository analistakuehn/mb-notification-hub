using System.Text.Json;
using NotificationHub.Api.Infrastructure.Cryptography;
using NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;

namespace NotificationHub.Api.Modules.Notifications.Features.Pipeline.Stages;

/// <summary>
/// First stage: TTL still valid, template still published for the
/// application and class, variables still passing the published schema. The
/// ingestion already gated all of this once; the pipeline revalidates by
/// design because publication state may have moved in between. The encrypted
/// variables are opened here, so every later stage reads plaintext from the
/// context and never from the store.
/// </summary>
internal sealed class ValidateStage(
    PublishedTemplateGate templateGate,
    IEnvelopeCipher cipher,
    TimeProvider timeProvider) : INotificationStage
{
    public string Name => "Validate";

    public async Task<StageOutcome> ExecuteAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        if (context.Notification.ExpiresAt <= timeProvider.GetUtcNow())
        {
            context.MarkExpired();
            return StageOutcome.Reject;
        }

        JsonElement? variables = await DecryptVariablesAsync(context, cancellationToken);
        TemplateGateOutcome gate = await templateGate.EvaluateAsync(
            context.Notification.Application,
            context.Notification.TemplateKey,
            context.Notification.Class,
            variables,
            // The bus restriction is an ingress rule: whatever reaches the
            // pipeline was already accepted through a transport allowed to
            // carry it, and re-applying the rule here would reject stored work.
            allowSensitiveVariables: true,
            cancellationToken);
        if (gate is TemplateGateOutcome.Rejected rejection)
        {
            context.LastReason = rejection.Reason;
            return StageOutcome.Reject;
        }

        context.Template = ((TemplateGateOutcome.Approved)gate).Template;
        context.Variables = variables;
        return StageOutcome.Continue;
    }

    private async Task<JsonElement?> DecryptVariablesAsync(
        NotificationContext context,
        CancellationToken cancellationToken)
    {
        if (context.Notification.VariablesEncrypted is not { Length: > 0 } sealedVariables)
        {
            return null;
        }

        var plaintext = await cipher.DecryptAsync(
            context.Notification.Application, sealedVariables, cancellationToken);
        using JsonDocument document = JsonDocument.Parse(plaintext);
        return document.RootElement.Clone();
    }
}
