namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

internal enum AttachmentObjectCaptureStatus
{
    /// <summary>The write placed a generation and the store named it.</summary>
    Captured,

    /// <summary>The key was already taken, so this call placed nothing.</summary>
    AlreadyExists,

    /// <summary>
    /// The write went through and the store did not name a generation, so the
    /// bytes exist without an identity this module can pin.
    /// </summary>
    Unidentified,

    /// <summary>
    /// The source ran out before delivering the length the request declared,
    /// so the transport dropped the write. The failure belongs to the request
    /// that sent too few bytes, and the store was never the problem.
    /// </summary>
    ContentShorterThanDeclared,

    /// <summary>The store could not be reached or refused the write.</summary>
    Unavailable,
}

/// <summary>Outcome of one write, carrying a locator only when one was captured.</summary>
internal sealed record AttachmentObjectCapture
{
    private AttachmentObjectCapture(
        AttachmentObjectCaptureStatus status,
        AttachmentObjectLocator? locator)
    {
        Status = status;
        Locator = locator;
    }

    internal AttachmentObjectCaptureStatus Status { get; }

    internal AttachmentObjectLocator? Locator { get; }

    internal static AttachmentObjectCapture Captured(AttachmentObjectLocator locator)
        => new(AttachmentObjectCaptureStatus.Captured, locator);

    internal static AttachmentObjectCapture AlreadyExists()
        => new(AttachmentObjectCaptureStatus.AlreadyExists, null);

    internal static AttachmentObjectCapture Unidentified()
        => new(AttachmentObjectCaptureStatus.Unidentified, null);

    internal static AttachmentObjectCapture ContentShorterThanDeclared()
        => new(AttachmentObjectCaptureStatus.ContentShorterThanDeclared, null);

    internal static AttachmentObjectCapture Unavailable()
        => new(AttachmentObjectCaptureStatus.Unavailable, null);
}
