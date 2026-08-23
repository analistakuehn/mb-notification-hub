using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;

/// <summary>Stable rejection reasons the gate reports; each one is also the problem type of the 422.</summary>
internal static class TemplateGateReasons
{
    internal const string NotFound = "template-not-found";
    internal const string ClassMismatch = "template-class-mismatch";
    internal const string VariablesInvalid = "template-variables-invalid";
}

/// <summary>Outcome of gating one ingestion request against the published catalog.</summary>
internal abstract record TemplateGateOutcome
{
    private TemplateGateOutcome()
    {
    }

    /// <summary>The template admits the request; decision metadata attached.</summary>
    internal sealed record Approved(PublishedTemplate Template) : TemplateGateOutcome;

    /// <summary>
    /// The request must be rejected; <see cref="Reason"/> is the stable
    /// rejection type and <see cref="Checks"/> carries the failed variable
    /// checks when the reason is the variables report.
    /// </summary>
    internal sealed record Rejected(
        string Reason,
        string Detail,
        IReadOnlyList<VariablesValidationCheck>? Checks = null) : TemplateGateOutcome;
}

/// <summary>
/// First validation gate of the ingestion over the published catalog of
/// TemplateManagement, read exclusively through its versioned contracts: the
/// template must exist for the application with a published version, must not
/// be deprecated or disabled, must belong to the requested class, and the
/// variables payload must pass the published schema. The Core pipeline
/// revalidates later by design; this gate only keeps garbage out of the door.
/// </summary>
internal sealed class PublishedTemplateGate(
    IPublishedCatalog catalog,
    IPublishedVariablesValidator variablesValidator)
{
    public async Task<TemplateGateOutcome> EvaluateAsync(
        string application,
        string templateKey,
        string canonicalClass,
        JsonElement? variables,
        CancellationToken cancellationToken)
    {
        Result<PublishedTemplateLookup> lookup =
            await catalog.FindTemplateAsync(application, templateKey, cancellationToken);
        if (lookup.IsFailure)
        {
            return new TemplateGateOutcome.Rejected(
                TemplateGateReasons.NotFound,
                $"A aplicação '{application}' não possui template publicado com a chave '{templateKey}'.");
        }

        if (lookup.Value is PublishedTemplateLookup.Rejected catalogRejection)
        {
            return new TemplateGateOutcome.Rejected(
                catalogRejection.Reason,
                $"O template '{templateKey}' não aceita novas solicitações ({catalogRejection.Reason}).");
        }

        PublishedTemplate template = ((PublishedTemplateLookup.Published)lookup.Value!).Template;
        if (!string.Equals(template.Class, canonicalClass, StringComparison.Ordinal))
        {
            return new TemplateGateOutcome.Rejected(
                TemplateGateReasons.ClassMismatch,
                $"O template '{templateKey}' pertence à classe '{template.Class}', não à classe pedida '{canonicalClass}'.");
        }

        Result<VariablesValidationReport> report =
            await variablesValidator.ValidateAsync(application, templateKey, variables, cancellationToken);
        if (report.IsFailure)
        {
            return new TemplateGateOutcome.Rejected(
                TemplateGateReasons.NotFound,
                $"A aplicação '{application}' não possui template publicado com a chave '{templateKey}'.");
        }

        if (!report.Value!.Passed)
        {
            return new TemplateGateOutcome.Rejected(
                TemplateGateReasons.VariablesInvalid,
                "As variáveis da solicitação não passam no esquema publicado do template.",
                report.Value!.Checks);
        }

        return new TemplateGateOutcome.Approved(template);
    }
}
