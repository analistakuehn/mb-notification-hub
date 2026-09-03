namespace NotificationHub.Api.Modules.AttachmentManagement.Features.Attachments;

internal static partial class RegisterAttachment
{
    internal sealed record Command(
        string Application,
        string FileName,
        string ContentType,
        long SizeBytes);
}
