using NotificationHub.SharedKernel;

namespace NotificationHub.Api.Modules.TemplateManagement.Integration.V1;

/// <summary>
/// In-process render of the published version of a template for sibling
/// modules, reusing the sandboxed engine and the locale fallback chain, with
/// the layout version pinned by the template version applied around the body.
/// </summary>
public interface IPublishedTemplateRenderer
{
    /// <summary>
    /// Renders the published version for one channel and locale. A deprecated
    /// or disabled template fails as a business-rule violation, because
    /// nothing may render from an identity that rejects new requests.
    /// </summary>
    Task<Result<PublishedTemplateRender>> RenderAsync(
        PublishedRenderRequest request,
        CancellationToken cancellationToken);
}
