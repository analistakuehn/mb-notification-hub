namespace NotificationHub.Api.Modules.AttachmentManagement.Infrastructure.Storage;

/// <summary>Everything the store needs to place one object, and nothing else.</summary>
internal sealed record AttachmentObjectRequest(
    Guid ContentId,
    string ContentType,
    long ExpectedSizeBytes);
