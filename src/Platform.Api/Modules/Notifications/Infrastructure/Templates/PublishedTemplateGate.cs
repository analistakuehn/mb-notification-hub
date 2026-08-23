using System.Text.Json;
using NotificationHub.Api.Modules.Notifications.Integration.V1;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.Notifications.Infrastructure.Templates;

/// <summary>
/// Stable rejection reasons the gate reports, aliased from the canonical
/// catalog: each one is also the problem type of the 422 on the REST path and
/// the <c>reason</c> of the rejection event on the bus path.
/// </summary>
internal static class TemplateGateReasons
{
    internal const string NotFound = NotificationRejectionReasons.TemplateNotFound;
    internal const string ClassMismatch = NotificationRejectionReasons.TemplateClassMismatch;
    internal const string VariablesInvalid = NotificationRejectionReasons.TemplateVariablesInvalid;
    internal const string SensitiveVariablesOnBus = NotificationRejectionReasons.SensitiveVariablesOnBus;
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
    /// rejection type, <see cref="Checks"/> carries the failed variable
    /// checks when the reason is the variables report, and
    /// <see cref="SensitiveVariables"/> carries the declared sensitive
    /// variable names when the reason is the bus restriction.
    /// </summary>
    internal sealed record Rejected(
        string Reason,
        string Detail,
        IReadOnlyList<VariablesValidationCheck>? Checks = null,
        IReadOnlyList<string>? SensitiveVariables = null) : TemplateGateOutcome;
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
    /// <remarks>
    /// <c>allowSensitiveVariables</c> says whether the caller's transport may
    /// carry a template that declares sensitive variables. The check depends
    /// only on the declaration, never
    /// on the payload: a rule that had to look at the values would be
    /// undecidable for the producer and would force the gate to inspect
    /// exactly the data it exists to protect. It runs before the schema
    /// validation for the same reason, since the validation reports checks
    /// over that payload.
    /// </remarks>
    public async Task<TemplateGateOutcome> EvaluateAsync(
        string application,
        string templateKey,
        string canonicalClass,
        JsonElement? variables,
        bool allowSensitiveVariables,
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

        if (!allowSensitiveVariables && template.SensitiveVariables.Count > 0)
        {
            return new TemplateGateOutcome.Rejected(
                TemplateGateReasons.SensitiveVariablesOnBus,
                $"O template '{templateKey}' declara variáveis sensíveis e só aceita solicitação por REST.",
                Checks: null,
                SensitiveVariables: template.SensitiveVariables);
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
