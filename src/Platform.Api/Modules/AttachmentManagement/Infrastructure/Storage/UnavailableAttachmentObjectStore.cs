namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>Stands in when no store is configured, and never claims custody.</summary>
internal sealed class UnavailableAttachmentObjectStore : IAttachmentObjectStore
{
    public Task<AttachmentObjectCapture> PutAsync(
        AttachmentObjectRequest request,
        Stream content,
        CancellationToken cancellationToken)
    {
        _ = request;
        _ = content;
        _ = cancellationToken;
        return Task.FromResult(AttachmentObjectCapture.Unavailable());
    }

    public Task<AttachmentStoreOpen> OpenAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken)
    {
        _ = locator;
        _ = cancellationToken;
        return Task.FromResult(AttachmentStoreOpen.Unavailable());
    }

    public Task<AttachmentObjectDiscard> DiscardAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken)
    {
        _ = locator;
        _ = cancellationToken;
        return Task.FromResult(AttachmentObjectDiscard.Unavailable);
    }
}
