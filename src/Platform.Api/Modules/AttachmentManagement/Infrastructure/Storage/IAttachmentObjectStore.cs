namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// Custody of attachment bytes. Every operation after the write names the
/// exact generation it acts on, so no call can reach whatever the key points
/// at now.
/// </summary>
internal interface IAttachmentObjectStore
{
    Task<AttachmentObjectCapture> PutAsync(
        AttachmentObjectRequest request,
        Stream content,
        CancellationToken cancellationToken);

    Task<AttachmentStoreOpen> OpenAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken);

    /// <summary>
    /// Removes one pinned generation and answers whether the store confirmed
    /// it. The answer exists because a caller that reports the bytes as gone
    /// has to have been told so: a removal that failed and a removal that
    /// succeeded are indistinguishable to anyone who only sees the call
    /// return.
    /// </summary>
    Task<AttachmentObjectDiscard> DiscardAsync(
        AttachmentObjectLocator locator,
        CancellationToken cancellationToken);
}
