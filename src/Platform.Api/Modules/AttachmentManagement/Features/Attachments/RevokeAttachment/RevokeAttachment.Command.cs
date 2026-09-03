namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RevokeAttachment
{
    /// <summary>
    /// The body of a revocation. The attachment travels in the address and the
    /// declared reason travels here, because the reason is the one thing the
    /// module cannot derive: taking a release back is always possible from the
    /// released state, so why it happened is the only part of the act worth
    /// recording.
    /// </summary>
    internal sealed record Command(string Reason);
}
