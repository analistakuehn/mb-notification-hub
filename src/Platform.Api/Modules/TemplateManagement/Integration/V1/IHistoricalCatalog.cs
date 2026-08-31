using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// In-process read surface of the catalog by exact version, for a consumer
/// reconstructing what happened rather than deciding what to send. It is a
/// contract of its own, separate from <see cref="IPublishedCatalog"/>, because
/// the two answer opposite questions: the published catalog answers "what would
/// go out now", this one answers "what went out then", and mixing them is how an
/// audit surface starts quoting a version nobody used.
/// </summary>
public interface IHistoricalCatalog
{
    /// <summary>
    /// Finds one exact version of a template of the application, published or
    /// superseded, with the layout version it pinned. An unknown application,
    /// template or version fails as not found, and so does a version that never
    /// left draft: it shipped nothing, so it is not part of what an old
    /// notification can be reconstructed from. The pinned layout follows the
    /// same rule and is omitted when it does not meet it.
    /// </summary>
    Task<Result<HistoricalTemplateVersion>> FindTemplateVersionAsync(
        string application,
        string templateKey,
        int version,
        CancellationToken cancellationToken);
}
