using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// In-process read surface of the published catalog for sibling modules: the
/// decision metadata of the template a producer referenced and the class
/// policy applied to every request of a class. Only published state is
/// visible; drafts and superseded versions never leave this module.
/// </summary>
public interface IPublishedCatalog
{
    /// <summary>
    /// Finds the published decision metadata for (application, templateKey).
    /// A deprecated or disabled template succeeds with the catalog rejection
    /// reason; a template the application does not own fails as not found, and
    /// so does an active template without a published version.
    /// </summary>
    Task<Result<PublishedTemplateLookup>> FindTemplateAsync(
        string application,
        string templateKey,
        CancellationToken cancellationToken);

    /// <summary>
    /// Finds the published class policy for (application, class), reading the
    /// stored definition through the tolerant version-1 vocabulary.
    /// </summary>
    Task<Result<PublishedClassPolicy>> FindClassPolicyAsync(
        string application,
        string notificationClass,
        CancellationToken cancellationToken);
}
