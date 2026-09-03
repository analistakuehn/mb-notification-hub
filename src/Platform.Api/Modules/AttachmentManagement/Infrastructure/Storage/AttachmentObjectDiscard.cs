namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>
/// What the store answered about removing one pinned generation. There is no
/// third answer on purpose: a removal the store did not confirm is not a
/// removal, and the bytes have to be counted as still there until some later
/// call confirms otherwise.
/// </summary>
internal enum AttachmentObjectDiscard
{
    /// <summary>The store confirmed the removal of that exact generation.</summary>
    Removed,

    /// <summary>The store could not be reached, or refused the removal.</summary>
    Unavailable,
}
