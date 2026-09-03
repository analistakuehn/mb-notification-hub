using NotificationHub.Api.Modules.AttachmentManagement.Domain;

namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class UploadAttachment
{
    internal sealed record Response(string Reference, string State)
    {
        internal static Response From(Attachment attachment)
            => new(attachment.Reference.Value, attachment.State);
    }
}
