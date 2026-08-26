using System.Text.Json;
using NotificationHub.Api.Modules.TemplateManagement.Domain;
using NotificationHub.Api.Modules.TemplateManagement.Infrastructure.ErrorHandling;
using NotificationHub.Api.Modules.TemplateManagement.Integration.V1;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Infrastructure.Integration;

/// <summary>
/// Validates a variables payload against the variables schema of the
/// published version, projecting the domain report into the published checks
/// vocabulary. It reads the published context through the shared loader, so a
/// caller that also renders the same template pays for that context once.
/// </summary>
internal sealed class PublishedVariablesValidator(PublishedContextLoader contextLoader)
    : IPublishedVariablesValidator
{
    public async Task<Result<VariablesValidationReport>> ValidateAsync(
        string application,
        string templateKey,
        JsonElement? variables,
        CancellationToken cancellationToken)
    {
        Result<PublishedTemplateContext> context =
            await contextLoader.LoadAsync(application, templateKey, cancellationToken);
        if (context.IsFailure)
        {
            return context.AsFailure<PublishedTemplateContext, VariablesValidationReport>();
        }

        ValidationReport report = VariablesPayloadValidation.Validate(
            context.Value!.Template,
            context.Value!.Version.VariablesSchemaJson,
            variables);
        return Result.Success(new VariablesValidationReport(report.Checks
            .Select(check => new VariablesValidationCheck(check.Name, check.Status, check.Message, check.Location))
            .ToList()));
    }
}
