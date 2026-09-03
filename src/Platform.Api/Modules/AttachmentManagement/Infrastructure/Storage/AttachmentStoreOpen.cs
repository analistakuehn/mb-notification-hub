namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

internal enum AttachmentStoreOpenStatus
{
    Opened,
    Missing,
    Unavailable,
}

/// <summary>
/// One reading of a pinned generation. Disposing it releases whatever the
/// store handed over along with the stream.
/// </summary>
internal sealed class AttachmentStoreOpen : IDisposable
{
    private readonly IDisposable? _owner;

    private AttachmentStoreOpen(
        AttachmentStoreOpenStatus status,
        Stream? content,
        IDisposable? owner)
    {
        Status = status;
        Content = content;
        _owner = owner;
    }

    internal AttachmentStoreOpenStatus Status { get; }

    internal Stream? Content { get; }

    internal static AttachmentStoreOpen Opened(Stream content, IDisposable? owner)
        => new(AttachmentStoreOpenStatus.Opened, content, owner);

    internal static AttachmentStoreOpen Missing()
        => new(AttachmentStoreOpenStatus.Missing, null, null);

    internal static AttachmentStoreOpen Unavailable()
        => new(AttachmentStoreOpenStatus.Unavailable, null, null);

    public void Dispose()
    {
        Content?.Dispose();
        _owner?.Dispose();
    }
}
