namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RevokeAttachment
{
    internal sealed record Response(string Reference, string State);
}
