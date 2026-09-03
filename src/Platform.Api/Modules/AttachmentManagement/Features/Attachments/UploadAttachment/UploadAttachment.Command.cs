namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class UploadAttachment
{
    internal sealed record Command(
        string Reference,
        Stream Content,
        long? DeclaredSizeBytes);
}
