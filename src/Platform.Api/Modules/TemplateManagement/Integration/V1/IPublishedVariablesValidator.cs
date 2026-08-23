using System.Text.Json;
using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// In-process validation of a producer variables payload against the
/// variables schema of the published version, with the same semantics as the
/// governed validation catalog: a provided variable the schema does not
/// declare fails, every required declaration must be provided, declared types
/// must match, and URL variables must stay inside the template's allowed
/// domains.
/// </summary>
public interface IPublishedVariablesValidator
{
    /// <summary>
    /// Validates the payload against the published version of
    /// (application, templateKey). A deprecated or disabled template fails as
    /// a business-rule violation, because nothing may validate against an
    /// identity that rejects new requests.
    /// </summary>
    Task<Result<VariablesValidationReport>> ValidateAsync(
        string application,
        string templateKey,
        JsonElement? variables,
        CancellationToken cancellationToken);
}
